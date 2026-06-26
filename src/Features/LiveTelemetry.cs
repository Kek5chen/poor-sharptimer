using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using FixVectorLeak;
using StackExchange.Redis;

namespace SharpTimer
{
    public partial class SharpTimer
    {
        private const string LiveTelemetryKey = "cs2:surf:live:latest";
        private const string LiveTelemetryChannel = "cs2:surf:live";

        private string? liveTelemetrySocketPath;
        private DateTime liveTelemetryNextRetryAt = DateTime.MinValue;
        private ConnectionMultiplexer? liveTelemetryRedis;
        private IDatabase? liveTelemetryDatabase;
        private ISubscriber? liveTelemetrySubscriber;
        private string? liveTelemetryLastPayload;
        private bool liveTelemetryMissingSocketLogged;
        private long liveTelemetryLastPublishAt;
        private long liveTelemetryLastProgressRefreshAt;
        private string? liveTelemetryQueuedPayload;
        private readonly object liveTelemetryPublishSync = new();
        private int liveTelemetryPublishWorkerActive;
        private bool liveTelemetryProgressMetadataScanned;
        private bool liveTelemetryProgressMetadataValid;

        private sealed class LiveSnapshot
        {
            [JsonPropertyName("server_name")]
            public string ServerName { get; set; } = string.Empty;

            [JsonPropertyName("updated_at")]
            public long UpdatedAt { get; set; }

            [JsonPropertyName("map")]
            public LiveMapState Map { get; set; } = new();

            [JsonPropertyName("human_players")]
            public int HumanPlayers { get; set; }

            [JsonPropertyName("active_players")]
            public int ActivePlayers { get; set; }

            [JsonPropertyName("players")]
            public List<LivePlayerState> Players { get; set; } = new();
        }

        private sealed class LiveMapState
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("display_name")]
            public string DisplayName { get; set; } = string.Empty;

            [JsonPropertyName("workshop_id")]
            public string WorkshopId { get; set; } = string.Empty;

            [JsonPropertyName("kind")]
            public string Kind { get; set; } = "linear";

            [JsonPropertyName("stage_count")]
            public int StageCount { get; set; } = 1;

            [JsonPropertyName("progress_anchors")]
            public List<LiveProgressAnchor> ProgressAnchors { get; set; } = new();
        }

        private sealed class LiveProgressAnchor
        {
            [JsonPropertyName("label")]
            public string Label { get; set; } = string.Empty;

            [JsonPropertyName("progress")]
            public double Progress { get; set; }

            [JsonPropertyName("kind")]
            public string Kind { get; set; } = "checkpoint";
        }

        private sealed class ProgressAnchor
        {
            public string Label { get; set; } = string.Empty;

            public Vector_t Position { get; set; } = new(0, 0, 0);

            public double Progress { get; set; }

            public string Kind { get; set; } = "checkpoint";

            public int? CheckpointIndex { get; set; }
        }

        private sealed class LivePlayerState
        {
            [JsonPropertyName("steam_id")]
            public string SteamId { get; set; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("stage_index")]
            public int StageIndex { get; set; }

            [JsonPropertyName("stage_count")]
            public int StageCount { get; set; }

            [JsonPropertyName("segment_progress")]
            public double SegmentProgress { get; set; }

            [JsonPropertyName("overall_progress")]
            public double OverallProgress { get; set; }

            [JsonPropertyName("timer_ticks")]
            public int TimerTicks { get; set; }
        }

        private void InitializeLiveTelemetry()
        {
            if (liveTelemetryRedis is { IsConnected: true } && liveTelemetryDatabase != null && liveTelemetrySubscriber != null)
                return;

            if (DateTime.UtcNow < liveTelemetryNextRetryAt)
                return;

            liveTelemetrySocketPath ??= Environment.GetEnvironmentVariable("CS2_SURF_TELEMETRY_REDIS_SOCKET")?.Trim();
            if (string.IsNullOrWhiteSpace(liveTelemetrySocketPath))
            {
                if (!liveTelemetryMissingSocketLogged)
                {
                    Utils.LogDebug("Live telemetry disabled: CS2_SURF_TELEMETRY_REDIS_SOCKET is not set");
                    liveTelemetryMissingSocketLogged = true;
                }
                return;
            }

            try
            {
                DisposeLiveTelemetry();

                var options = new ConfigurationOptions
                {
                    AbortOnConnectFail = false,
                    ConnectRetry = 2,
                    ConnectTimeout = 2000,
                    SyncTimeout = 2000,
                };
                options.EndPoints.Add(new UnixDomainSocketEndPoint(liveTelemetrySocketPath));

                liveTelemetryRedis = ConnectionMultiplexer.Connect(options);
                liveTelemetryDatabase = liveTelemetryRedis.GetDatabase();
                liveTelemetrySubscriber = liveTelemetryRedis.GetSubscriber();
                liveTelemetryLastPayload = null;
                liveTelemetryNextRetryAt = DateTime.MinValue;
                Utils.LogDebug($"Live telemetry connected via {liveTelemetrySocketPath}");
            }
            catch (Exception ex)
            {
                DisposeLiveTelemetry();
                liveTelemetryNextRetryAt = DateTime.UtcNow.AddSeconds(10);
                Utils.LogError($"Live telemetry connect failed: {ex.Message}");
            }
        }

        private void DisposeLiveTelemetry()
        {
            try
            {
                liveTelemetryRedis?.Dispose();
            }
            catch
            {
            }
            finally
            {
                liveTelemetryRedis = null;
                liveTelemetryDatabase = null;
                liveTelemetrySubscriber = null;
                lock (liveTelemetryPublishSync)
                {
                    liveTelemetryQueuedPayload = null;
                }
                System.Threading.Interlocked.Exchange(ref liveTelemetryPublishWorkerActive, 0);
            }
        }

        private void QueuePublishLiveTelemetry(bool force = false)
        {
            try
            {
                InitializeLiveTelemetry();
                if (liveTelemetryDatabase == null || liveTelemetrySubscriber == null)
                    return;

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (!force && now - liveTelemetryLastPublishAt < 250)
                    return;

                liveTelemetryLastPublishAt = now;
                string payload = JsonSerializer.Serialize(BuildLiveTelemetrySnapshot());
                if (!force && payload == liveTelemetryLastPayload)
                    return;

                liveTelemetryLastPayload = payload;
                EnqueueLiveTelemetryPublish(payload);
            }
            catch (Exception ex)
            {
                Utils.LogError($"Error queueing live telemetry: {ex.Message}");
            }
        }

        private void EnqueueLiveTelemetryPublish(string payload)
        {
            lock (liveTelemetryPublishSync)
            {
                liveTelemetryQueuedPayload = payload;
            }

            if (System.Threading.Interlocked.CompareExchange(ref liveTelemetryPublishWorkerActive, 1, 0) != 0)
                return;

            _ = Task.Run(ProcessLiveTelemetryPublishQueue);
        }

        private async Task ProcessLiveTelemetryPublishQueue()
        {
            try
            {
                while (true)
                {
                    string? payload;
                    lock (liveTelemetryPublishSync)
                    {
                        payload = liveTelemetryQueuedPayload;
                        liveTelemetryQueuedPayload = null;
                    }

                    if (string.IsNullOrEmpty(payload))
                        return;

                    PublishLiveTelemetry(payload);
                    await Task.Yield();
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref liveTelemetryPublishWorkerActive, 0);

                bool hasPendingPayload;
                lock (liveTelemetryPublishSync)
                {
                    hasPendingPayload = !string.IsNullOrEmpty(liveTelemetryQueuedPayload);
                }

                if (hasPendingPayload && System.Threading.Interlocked.CompareExchange(ref liveTelemetryPublishWorkerActive, 1, 0) == 0)
                    _ = Task.Run(ProcessLiveTelemetryPublishQueue);
            }
        }

        private bool HasValidLiveTelemetryProgressMetadata()
        {
            if (!liveTelemetryProgressMetadataScanned)
                return false;

            bool hasStart = currentRespawnPos.HasValue && !IsZeroVector(currentRespawnPos.Value);
            bool hasEnd = currentEndPos.HasValue && !IsZeroVector(currentEndPos.Value);
            if (!hasStart || !hasEnd)
                return false;

            if (string.Equals(currentMapType, "Staged", StringComparison.OrdinalIgnoreCase))
                return stageTriggerCount > 0;

            return true;
        }

        private void RefreshLiveTelemetryProgressMetadata(bool force = false)
        {
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (!force && now - liveTelemetryLastProgressRefreshAt < 5000)
                    return;

                if (!force && liveTelemetryProgressMetadataValid)
                    return;

                liveTelemetryLastProgressRefreshAt = now;
                entityCache.UpdateCache();

                if (!currentRespawnPos.HasValue || IsZeroVector(currentRespawnPos.Value))
                {
                    (currentRespawnPos, currentRespawnAng) = FindStartTriggerPos();
                }

                if (!currentEndPos.HasValue || IsZeroVector(currentEndPos.Value))
                {
                    currentEndPos = FindEndTriggerPos();
                }

                FindStageTriggers();
                FindCheckpointTriggers();
                liveTelemetryProgressMetadataScanned = true;
                liveTelemetryProgressMetadataValid = HasValidLiveTelemetryProgressMetadata();
            }
            catch (Exception ex)
            {
                Utils.LogError($"Error refreshing live telemetry progress metadata: {ex.Message}");
            }
        }

        private void PublishLiveTelemetry(string payload)
        {
            try
            {
                if (liveTelemetryDatabase == null || liveTelemetrySubscriber == null)
                    return;

                liveTelemetryDatabase.StringSet(LiveTelemetryKey, payload);
                liveTelemetrySubscriber.Publish(RedisChannel.Literal(LiveTelemetryChannel), payload);
            }
            catch (Exception ex)
            {
                Utils.LogError($"Error publishing live telemetry: {ex.Message}");
                DisposeLiveTelemetry();
                liveTelemetryNextRetryAt = DateTime.UtcNow.AddSeconds(10);
            }
        }

        private LiveSnapshot BuildLiveTelemetrySnapshot()
        {
            RefreshLiveTelemetryProgressMetadata();

            bool staged = stageTriggerCount > 0
                || string.Equals(currentMapType, "Staged", StringComparison.OrdinalIgnoreCase);
            int stageCount = stageTriggerCount > 0
                ? stageTriggerCount
                : cpTriggerCount > 0
                    ? cpTriggerCount + 1
                    : 1;
            var displayAnchors = staged
                ? BuildStagedDisplayProgressAnchors(stageCount)
                : BuildLinearProgressAnchors();
            var snapshot = new LiveSnapshot
            {
                ServerName = ConVar.Find("hostname")?.StringValue ?? defaultServerHostname ?? "cssurf.club",
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Map = new LiveMapState
                {
                    Name = currentMapName ?? Server.MapName ?? string.Empty,
                    DisplayName = (currentMapName ?? Server.MapName ?? string.Empty).Replace("_", " "),
                    WorkshopId = currentAddonID ?? string.Empty,
                    Kind = staged ? "staged" : "linear",
                    StageCount = stageCount,
                    ProgressAnchors = displayAnchors
                        .Select(anchor => new LiveProgressAnchor
                        {
                            Label = anchor.Label,
                            Progress = anchor.Progress,
                            Kind = anchor.Kind,
                        })
                        .ToList(),
                },
            };

            foreach (var player in connectedPlayers.Values)
            {
                if (!IsHumanConnectedPlayer(player))
                    continue;

                snapshot.HumanPlayers++;

                if (TryBuildLivePlayerState(player, stageCount, staged, out var playerState))
                    snapshot.Players.Add(playerState);
            }

            snapshot.Players = snapshot.Players
                .OrderByDescending(player => player.OverallProgress)
                .ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            snapshot.ActivePlayers = snapshot.Players.Count;

            return snapshot;
        }

        private bool IsHumanConnectedPlayer(CCSPlayerController? player)
        {
            return player != null && player.IsValid && !player.IsBot && !player.IsHLTV && player.Connected == PlayerConnectedState.Connected;
        }

        private bool TryBuildLivePlayerState(CCSPlayerController player, int stageCount, bool staged, out LivePlayerState playerState)
        {
            playerState = new LivePlayerState();

            if (!playerTimers.TryGetValue(player.Slot, out var playerTimer))
                return false;

            if (!playerTimer.IsTimerRunning || playerTimer.IsBonusTimerRunning)
                return false;

            var pawn = player.PlayerPawn?.Value;
            var absOrigin = pawn?.CBodyComponent?.SceneNode?.AbsOrigin;
            if (absOrigin == null)
                return false;

            Vector_t position = absOrigin.ToVector_t();
            int stageIndex = staged ? Math.Clamp(playerTimer.CurrentMapStage == 0 ? 1 : playerTimer.CurrentMapStage, 1, stageCount) : 1;
            double segmentProgress = 0.0d;
            double overallProgress = 0.0d;

            bool computed = staged
                ? TryComputeStageProgress(position, playerTimer, stageIndex, stageCount, out segmentProgress, out overallProgress)
                : TryComputeLinearProgress(position, playerTimer, out segmentProgress, out overallProgress);
            if (!computed)
                return false;

            playerState = new LivePlayerState
            {
                SteamId = player.SteamID.ToString(),
                Name = player.PlayerName,
                StageIndex = stageIndex,
                StageCount = stageCount,
                SegmentProgress = segmentProgress,
                OverallProgress = overallProgress,
                TimerTicks = playerTimer.TimerTicks,
            };
            return true;
        }

        private List<ProgressAnchor> BuildStagedDisplayProgressAnchors(int stageCount)
        {
            var anchors = new List<ProgressAnchor>
            {
                new() { Label = "Start", Progress = 0.0d, Kind = "start" },
            };

            var checkpointStages = BuildCheckpointStageAssignments(stageCount);
            for (int stageIndex = 1; stageIndex <= stageCount; stageIndex++)
            {
                if (TryGetStageBounds(stageIndex, stageCount, out var stageStart, out var stageEnd))
                {
                    foreach (var checkpointAnchor in BuildStageMicroAnchors(stageIndex, stageCount, stageStart, stageEnd, checkpointStages)
                                 .Where(anchor => anchor.CheckpointIndex.HasValue))
                    {
                        anchors.Add(new ProgressAnchor
                        {
                            Label = $"CP {checkpointAnchor.CheckpointIndex!.Value}",
                            Kind = "checkpoint",
                            Progress = Math.Clamp(((stageIndex - 1) + checkpointAnchor.Progress) / Math.Max(stageCount, 1), 0.0d, 1.0d),
                            CheckpointIndex = checkpointAnchor.CheckpointIndex,
                        });
                    }
                }

                anchors.Add(new ProgressAnchor
                {
                    Label = stageIndex >= stageCount ? "Finish" : $"Stage {stageIndex}",
                    Kind = stageIndex >= stageCount ? "finish" : "stage",
                    Progress = Math.Clamp((double)stageIndex / Math.Max(stageCount, 1), 0.0d, 1.0d),
                });
            }

            return anchors
                .OrderBy(anchor => anchor.Progress)
                .ThenBy(anchor => anchor.Kind == "checkpoint" ? 1 : 0)
                .ToList();
        }

        private bool TryComputeLinearProgress(Vector_t position, PlayerTimerInfo playerTimer, out double segmentProgress, out double overallProgress)
        {
            segmentProgress = 0.0d;
            overallProgress = 0.0d;

            var anchors = BuildLinearProgressAnchors();
            if (anchors.Count < 2)
                return false;

            int segmentIndex = Math.Clamp(playerTimer.CurrentMapCheckpoint, 0, anchors.Count - 2);
            return TryComputeProgressFromAnchors(position, anchors, segmentIndex, out segmentProgress, out overallProgress);
        }

        private List<ProgressAnchor> BuildLinearProgressAnchors()
        {
            Vector_t? start = GetMainCourseStart();
            Vector_t? end = GetMainCourseEnd();
            if (!start.HasValue || !end.HasValue)
                return [];

            var anchors = new List<ProgressAnchor>
            {
                new() { Label = "Start", Position = start.Value, Kind = "start" },
            };

            foreach (var checkpointIndex in cpTriggerPoses.Keys.OrderBy(index => index))
            {
                Vector_t? checkpointPosition = cpTriggerPoses.GetValueOrDefault(checkpointIndex);
                if (!checkpointPosition.HasValue)
                    continue;

                anchors.Add(new ProgressAnchor
                {
                    Label = $"CP {checkpointIndex}",
                    Position = checkpointPosition.Value,
                    Kind = "checkpoint",
                    CheckpointIndex = checkpointIndex,
                });
            }

            anchors.Add(new ProgressAnchor { Label = "Finish", Position = end.Value, Kind = "finish" });
            return NormalizeProgressAnchors(anchors);
        }

        private bool TryComputeStageProgress(Vector_t position, PlayerTimerInfo playerTimer, int stageIndex, int stageCount, out double segmentProgress, out double overallProgress)
        {
            segmentProgress = 0.0d;
            overallProgress = 0.0d;

            if (!TryGetStageBounds(stageIndex, stageCount, out var stageStart, out var stageEnd))
                return false;

            var checkpointStages = BuildCheckpointStageAssignments(stageCount);
            var anchors = BuildStageMicroAnchors(stageIndex, stageCount, stageStart, stageEnd, checkpointStages);
            if (anchors.Count < 2)
                return false;

            int segmentIndex = Math.Clamp(
                anchors.Count(anchor => anchor.CheckpointIndex.HasValue && anchor.CheckpointIndex.Value <= playerTimer.CurrentMapCheckpoint),
                0,
                anchors.Count - 2
            );

            if (!TryComputeProgressFromAnchors(position, anchors, segmentIndex, out segmentProgress, out double stageProgress))
                return false;

            overallProgress = Math.Clamp(((stageIndex - 1) + stageProgress) / Math.Max(stageCount, 1), 0.0d, 1.0d);
            return true;
        }

        private List<ProgressAnchor> BuildStageMicroAnchors(int stageIndex, int stageCount, Vector_t stageStart, Vector_t stageEnd, Dictionary<int, int> checkpointStages)
        {
            var anchors = new List<ProgressAnchor>
            {
                new() { Label = $"Stage {stageIndex} Start", Position = stageStart },
            };

            foreach (var checkpointIndex in cpTriggerPoses.Keys.OrderBy(index => index))
            {
                if (checkpointStages.GetValueOrDefault(checkpointIndex) != stageIndex)
                    continue;

                Vector_t? checkpointPosition = cpTriggerPoses.GetValueOrDefault(checkpointIndex);
                if (!checkpointPosition.HasValue)
                    continue;

                anchors.Add(new ProgressAnchor
                {
                    Label = $"CP {checkpointIndex}",
                    Position = checkpointPosition.Value,
                    CheckpointIndex = checkpointIndex,
                });
            }

            anchors.Add(new ProgressAnchor
            {
                Label = stageIndex >= stageCount ? "Finish" : $"Stage {stageIndex} End",
                Position = stageEnd,
            });
            return NormalizeProgressAnchors(anchors);
        }

        private Dictionary<int, int> BuildCheckpointStageAssignments(int stageCount)
        {
            var assignments = new Dictionary<int, int>();
            if (cpTriggerPoses.Count == 0)
                return assignments;

            if (stageCount <= 1)
            {
                foreach (var checkpointIndex in cpTriggerPoses.Keys.OrderBy(index => index))
                    assignments[checkpointIndex] = 1;
                return assignments;
            }

            int minimumStage = 1;
            foreach (var checkpointIndex in cpTriggerPoses.Keys.OrderBy(index => index))
            {
                Vector_t? checkpointPosition = cpTriggerPoses.GetValueOrDefault(checkpointIndex);
                if (!checkpointPosition.HasValue)
                    continue;

                int bestStage = minimumStage;
                double bestScore = double.MaxValue;

                for (int stageIndex = minimumStage; stageIndex <= stageCount; stageIndex++)
                {
                    if (!TryGetStageBounds(stageIndex, stageCount, out var stageStart, out var stageEnd))
                        continue;

                    double score = ScoreCheckpointForStage(checkpointPosition.Value, stageStart, stageEnd);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestStage = stageIndex;
                    }
                }

                assignments[checkpointIndex] = bestStage;
                minimumStage = bestStage;
            }

            return assignments;
        }

        private static double ScoreCheckpointForStage(Vector_t checkpointPosition, Vector_t stageStart, Vector_t stageEnd)
        {
            double stageLength = DistanceBetween(stageStart, stageEnd);
            if (stageLength <= 0.0001d)
                return double.MaxValue;

            double projection = ComputeRawProjectionProgress(checkpointPosition, stageStart, stageEnd);
            Vector_t closestPoint = PointAlong(stageStart, stageEnd, Math.Clamp(projection, 0.0d, 1.0d));
            double offset = DistanceBetween(checkpointPosition, closestPoint);
            double overflow = projection < 0.0d
                ? -projection
                : projection > 1.0d
                    ? projection - 1.0d
                    : 0.0d;
            return offset + (overflow * stageLength * 6.0d);
        }

        private static List<ProgressAnchor> NormalizeProgressAnchors(List<ProgressAnchor> anchors)
        {
            if (anchors.Count < 2)
                return anchors;

            double totalDistance = 0.0d;
            var segmentDistances = new List<double>();
            for (int index = 0; index < anchors.Count - 1; index++)
            {
                double distance = DistanceBetween(anchors[index].Position, anchors[index + 1].Position);
                segmentDistances.Add(distance);
                totalDistance += distance;
            }

            if (totalDistance <= 0.0001d)
            {
                for (int index = 0; index < anchors.Count; index++)
                {
                    anchors[index].Progress = anchors.Count == 1
                        ? 0.0d
                        : (double)index / (anchors.Count - 1);
                }
                return anchors;
            }

            double cumulativeDistance = 0.0d;
            anchors[0].Progress = 0.0d;
            for (int index = 1; index < anchors.Count; index++)
            {
                cumulativeDistance += segmentDistances[index - 1];
                anchors[index].Progress = Math.Clamp(cumulativeDistance / totalDistance, 0.0d, 1.0d);
            }

            return anchors;
        }

        private bool TryComputeProgressFromAnchors(Vector_t position, List<ProgressAnchor> anchors, int segmentIndex, out double segmentProgress, out double overallProgress)
        {
            segmentProgress = 0.0d;
            overallProgress = 0.0d;

            if (anchors.Count < 2)
                return false;

            segmentIndex = Math.Clamp(segmentIndex, 0, anchors.Count - 2);
            ProgressAnchor startAnchor = anchors[segmentIndex];
            ProgressAnchor endAnchor = anchors[segmentIndex + 1];
            segmentProgress = ComputeProjectionProgress(position, startAnchor.Position, endAnchor.Position);
            overallProgress = Math.Clamp(
                startAnchor.Progress + ((endAnchor.Progress - startAnchor.Progress) * segmentProgress),
                0.0d,
                1.0d
            );
            return true;
        }

        private bool TryGetStageBounds(int stageIndex, int stageCount, out Vector_t stageStart, out Vector_t stageEnd)
        {
            Vector_t? start = GetStageStart(stageIndex);
            Vector_t? end = stageIndex >= stageCount
                ? GetMainCourseEnd()
                : GetStageStart(stageIndex + 1);

            if (start.HasValue && end.HasValue)
            {
                stageStart = start.Value;
                stageEnd = end.Value;
                return true;
            }

            stageStart = new Vector_t(0, 0, 0);
            stageEnd = new Vector_t(0, 0, 0);
            return false;
        }

        private Vector_t? GetStageStart(int stageIndex)
        {
            if (stageTriggerPoses.TryGetValue(stageIndex, out Vector_t? stagePosition) && stagePosition.HasValue && !IsZeroVector(stagePosition.Value))
                return stagePosition.Value;

            if (stageIndex <= 1)
                return GetMainCourseStart();

            return null;
        }

        private bool TryGetMainCourseBounds(out Vector_t start, out Vector_t end)
        {
            Vector_t? startPoint = GetMainCourseStart();
            Vector_t? endPoint = GetMainCourseEnd();

            if (startPoint.HasValue && endPoint.HasValue)
            {
                start = startPoint.Value;
                end = endPoint.Value;
                return true;
            }

            start = new Vector_t(0, 0, 0);
            end = new Vector_t(0, 0, 0);
            return false;
        }

        private Vector_t? GetMainCourseStart()
        {
            if (!IsZeroVector(currentMapStartC1) && !IsZeroVector(currentMapStartC2))
                return Utils.CalculateMiddleVector_t(currentMapStartC1, currentMapStartC2);

            if (currentRespawnPos.HasValue && !IsZeroVector(currentRespawnPos.Value))
                return currentRespawnPos.Value;

            return null;
        }

        private Vector_t? GetMainCourseEnd()
        {
            if (!IsZeroVector(currentMapEndC1) && !IsZeroVector(currentMapEndC2))
                return Utils.CalculateMiddleVector_t(currentMapEndC1, currentMapEndC2);

            if (currentEndPos.HasValue && !IsZeroVector(currentEndPos.Value))
                return currentEndPos.Value;

            return null;
        }

        private static double ComputeProjectionProgress(Vector_t position, Vector_t start, Vector_t end)
        {
            return Math.Clamp(ComputeRawProjectionProgress(position, start, end), 0.0d, 1.0d);
        }

        private static double ComputeRawProjectionProgress(Vector_t position, Vector_t start, Vector_t end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double dz = end.Z - start.Z;
            double lengthSquared = (dx * dx) + (dy * dy) + (dz * dz);
            if (lengthSquared <= 0.0001d)
                return 0.5d;

            double px = position.X - start.X;
            double py = position.Y - start.Y;
            double pz = position.Z - start.Z;
            return ((px * dx) + (py * dy) + (pz * dz)) / lengthSquared;
        }

        private static Vector_t PointAlong(Vector_t start, Vector_t end, double progress)
        {
            float t = (float)Math.Clamp(progress, 0.0d, 1.0d);
            return new Vector_t(
                start.X + ((end.X - start.X) * t),
                start.Y + ((end.Y - start.Y) * t),
                start.Z + ((end.Z - start.Z) * t)
            );
        }

        private static double DistanceBetween(Vector_t start, Vector_t end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double dz = end.Z - start.Z;
            return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        private static bool IsZeroVector(Vector_t vector)
        {
            return vector.X == 0 && vector.Y == 0 && vector.Z == 0;
        }
    }
}

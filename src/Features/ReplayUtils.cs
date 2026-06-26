/*
Copyright (C) 2024 Dea Brcka

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using CounterStrikeSharp.API;
using System.Runtime.Serialization.Formatters.Binary;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using System.Text.Json;
using FixVectorLeak;
using CounterStrikeSharp.API.Modules.Timers;
using System.Drawing;

namespace SharpTimer
{
    public partial class SharpTimer
    {
        private void ReplayUpdate(CCSPlayerController player, int timerTicks)
        {
            try
            {
                if (!IsAllowedPlayer(player)) return;

                ReplayVector currentPosition = ReplayVector.GetVectorish(player.Pawn.Value!.CBodyComponent?.SceneNode?.AbsOrigin ?? new(0, 0, 0));
                ReplayVector currentSpeed = ReplayVector.GetVectorish(player.PlayerPawn.Value!.AbsVelocity);
                ReplayQAngle currentRotation = ReplayQAngle.GetQAngleish(player.PlayerPawn.Value.V_angle);

                var buttons = player.Buttons;
                var flags = player.Pawn.Value.Flags;
                var moveType = player.Pawn.Value.MoveType;
                var replayState = playerReplays[player.Slot];
                var sampleIndex = replayState.replayFrames.Count;
                var sampleTime = sampleIndex / 64.0;
                var duckAmount = playerTimers.TryGetValue(player.Slot, out var timer) && timer.MovementService != null
                    ? timer.MovementService.DuckAmount
                    : 0f;
                var grounded = ((PlayerFlags)flags & PlayerFlags.FL_ONGROUND) != 0;
                var teleportCut = false;

                if (sampleIndex > 0)
                {
                    var previousFrame = replayState.replayFrames[sampleIndex - 1];
                    if (previousFrame.Position != null)
                    {
                        var dx = currentPosition.X - previousFrame.Position.X;
                        var dy = currentPosition.Y - previousFrame.Position.Y;
                        var dz = currentPosition.Z - previousFrame.Position.Z;
                        teleportCut = ((dx * dx) + (dy * dy) + (dz * dz)) > 65536.0f;
                    }
                }

                var replayFrame = new PlayerReplays.ReplayFrames
                {
                    SampleIndex = sampleIndex,
                    SampleTime = sampleTime,
                    Position = currentPosition,
                    Rotation = currentRotation,
                    Speed = currentSpeed,
                    Buttons = buttons,
                    Flags = flags,
                    MoveType = moveType,
                    DuckAmount = duckAmount,
                    Grounded = grounded,
                    TimerTicks = timerTicks,
                    TeleportCut = teleportCut
                };

                replayState.replayFrames.Add(replayFrame);
            }
            catch (Exception ex)
            {
                Utils.LogError($"Error in ReplayUpdate: {ex.Message}");
            }
        }

        private void ReplayPlayback(CCSPlayerController player, int plackbackTick)
        {
            try
            {
                if (!IsAllowedPlayer(player)) return;

                //player.LerpTime = 0.0078125f;

                if (playerTimers.TryGetValue(player.Slot, out PlayerTimerInfo? value))
                {
                    var replayState = playerReplays[player.Slot];
                    var replayFrame = replayState.replayFrames[plackbackTick];
                    replayState.CurrentReplayFrame = replayFrame;

                    if (replayState.UseDetachedReplayView && replayState.ReplayCamera != null && replayState.ReplayCamera.IsValid)
                    {
                        UpdateDetachedReplayView(player, replayState, replayFrame);
                        PrintReplayButtons(player, replayFrame, value);
                        return;
                    }

                    if (((PlayerFlags)replayFrame.Flags & PlayerFlags.FL_ONGROUND) != 0)
                    {
                        SetMoveType(player, MoveType_t.MOVETYPE_WALK);
                    }
                    else
                    {
                        SetMoveType(player, MoveType_t.MOVETYPE_OBSERVER);
                    }

                    if (((PlayerFlags)replayFrame.Flags & PlayerFlags.FL_DUCKING) != 0)
                    {
                        value.MovementService!.DuckAmount = 1;
                    }
                    else
                    {
                        value.MovementService!.DuckAmount = 0;
                    }

                    var replayRotation = GetReplayPawnRotation(player, replayFrame.Rotation!);
                    player.PlayerPawn.Value!.Teleport(ReplayVector.ToVector(replayFrame.Position!), ReplayQAngle.ToQAngle(replayRotation), ReplayVector.ToVector(replayFrame.Speed!));
                    EmitReplayBotTrail(player, playerReplays[player.Slot], replayFrame.Position, replayFrame.TeleportCut);

                    PrintReplayButtons(player, replayFrame, value);
                }
            }
            catch (Exception ex)
            {
                Utils.LogError($"Error in ReplayPlayback: {ex.Message}");
            }
        }

        private bool ShouldUseInterpolatedReplay(PlayerReplays replayState)
        {
            return replayState.UseInterpolatedPlayback && replayState.replayFrames.Count > 1 && replayState.replayFrames.Any(frame => frame.SampleTime > 0);
        }

        private static float LerpFloat(float from, float to, double alpha)
        {
            return (float)(from + ((to - from) * alpha));
        }

        private static float LerpAngle(float from, float to, double alpha)
        {
            var delta = to - from;
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;
            return (float)(from + (delta * alpha));
        }

        private ReplayQAngle GetReplayDisplayRotation(ReplayQAngle replayRotation)
        {
            return new ReplayQAngle(0f, replayRotation.Yaw, 0f);
        }

        private ReplayQAngle GetReplayPawnRotation(CCSPlayerController player, ReplayQAngle replayRotation)
        {
            if (replayBotController != null && replayBotController.Handle == player.Handle)
            {
                return GetReplayDisplayRotation(replayRotation);
            }

            return replayRotation;
        }

        private void PrintReplayButtons(CCSPlayerController player, PlayerReplays.ReplayFrames replayFrame, PlayerTimerInfo value)
        {
            var replayButtons = $"{((replayFrame.Buttons & PlayerButtons.Moveleft) != 0 ? "A" : "_")} " +
                                $"{((replayFrame.Buttons & PlayerButtons.Forward) != 0 ? "W" : "_")} " +
                                $"{((replayFrame.Buttons & PlayerButtons.Moveright) != 0 ? "D" : "_")} " +
                                $"{((replayFrame.Buttons & PlayerButtons.Back) != 0 ? "S" : "_")} " +
                                $"{((replayFrame.Buttons & PlayerButtons.Jump) != 0 ? "J" : "_")} " +
                                $"{((replayFrame.Buttons & PlayerButtons.Duck) != 0 ? "C" : "_")}";

            if (value.HideKeys != true && value.IsReplaying == true && keysOverlayEnabled == true)
            {
                player.PrintToCenter(replayButtons);
            }
        }

        private Vector GetDetachedReplayCameraPosition(CCSPlayerController player, ReplayVector replayPosition)
        {
            var eyeOffset = player.PlayerPawn?.Value?.ViewOffset.Z ?? 64f;
            return new Vector(replayPosition.X, replayPosition.Y, replayPosition.Z + eyeOffset);
        }

        private bool StartDetachedReplayView(CCSPlayerController player, PlayerReplays replayState)
        {
            ClearDetachedReplayView(player, replayState);

            var firstFrame = replayState.replayFrames.FirstOrDefault(frame => frame.Position != null && frame.Rotation != null);
            if (firstFrame?.Position == null || firstFrame.Rotation == null)
                return false;

            var camera = Utilities.CreateEntityByName<CBaseEntity>("point_viewcontrol");
            if (camera == null)
                return false;

            try
            {
                camera.DispatchSpawn();
                replayState.ReplayCamera = camera;
                replayState.UseDetachedReplayView = true;
                UpdateDetachedReplayView(player, replayState, firstFrame);
                camera.AcceptInput("Enable", player.PlayerPawn.Value!, player.PlayerPawn.Value!, "", 0);
                return true;
            }
            catch (Exception ex)
            {
                Utils.LogError($"Failed to start detached replay view: {ex.Message}");
                ClearDetachedReplayView(player, replayState);
                return false;
            }
        }

        private void ClearDetachedReplayView(CCSPlayerController? player, PlayerReplays replayState)
        {
            try
            {
                if (player != null && player.IsValid && player.PlayerPawn?.Value != null &&
                    replayState.ReplayCamera != null && replayState.ReplayCamera.IsValid)
                {
                    replayState.ReplayCamera.AcceptInput("Disable", player.PlayerPawn.Value, player.PlayerPawn.Value, "", 0);
                }
            }
            catch
            {
            }

            try
            {
                if (replayState.ReplayCamera != null && replayState.ReplayCamera.IsValid)
                    replayState.ReplayCamera.Remove();
            }
            catch
            {
            }

            replayState.ReplayCamera = null;
            replayState.UseDetachedReplayView = false;
            replayState.CurrentReplayFrame = null;
        }

        private void UpdateDetachedReplayView(CCSPlayerController player, PlayerReplays replayState, PlayerReplays.ReplayFrames replayFrame)
        {
            if (replayState.ReplayCamera == null || !replayState.ReplayCamera.IsValid || replayFrame.Position == null || replayFrame.Rotation == null || replayFrame.Speed == null)
                return;

            replayState.CurrentReplayFrame = replayFrame;
            replayState.ReplayCamera.Teleport(
                GetDetachedReplayCameraPosition(player, replayFrame.Position),
                ReplayQAngle.ToQAngle(replayFrame.Rotation),
                ReplayVector.ToVector(replayFrame.Speed)
            );
        }

        private static Vector_t ToVectorT(ReplayVector vector)
        {
            return new Vector_t(vector.X, vector.Y, vector.Z);
        }

        private void EmitReplayBotTrail(CCSPlayerController? player, PlayerReplays replayState, ReplayVector? currentPosition, bool teleportCut = false)
        {
            bool isSharedReplaySource =
                (player != null && replayBotController != null && replayBotController.Handle == player.Handle) ||
                (player == null && replayBotVisualEntity != null && replayBotVisualEntity.IsValid);

            if (!isSharedReplaySource || currentPosition == null)
            {
                replayState.LastTrailPosition = null;
                replayState.LastTrailEmitAtMilliseconds = 0;
                return;
            }

            if (teleportCut || replayState.LastTrailPosition == null)
            {
                replayState.LastTrailPosition = new ReplayVector(currentPosition.X, currentPosition.Y, currentPosition.Z);
                replayState.LastTrailEmitAtMilliseconds = Environment.TickCount64;
                return;
            }

            var now = Environment.TickCount64;
            if (now - replayState.LastTrailEmitAtMilliseconds < 80)
                return;

            CBeam beam = Utilities.CreateEntityByName<CBeam>("beam")!;
            if (beam == null)
                return;

            var start = ToVectorT(replayState.LastTrailPosition);
            var end = ToVectorT(currentPosition);
            beam.Render = Color.DodgerBlue;
            beam.Width = 2.0f;
            beam.FadeMinDist = 9999;
            beam.Teleport(start, new QAngle_t(0, 0, 0), new Vector_t(0, 0, 0));
            beam.EndPos.X = end.X;
            beam.EndPos.Y = end.Y;
            beam.EndPos.Z = end.Z;
            beam.DispatchSpawn();

            AddTimer(0.35f, () =>
            {
                try
                {
                    beam.Remove();
                }
                catch
                {
                }
            });

            replayState.LastTrailPosition = new ReplayVector(currentPosition.X, currentPosition.Y, currentPosition.Z);
            replayState.LastTrailEmitAtMilliseconds = now;
        }

        private PlayerReplays.ReplayFrames SampleReplayFrame(PlayerReplays replayState, double sampleTime)
        {
            var frames = replayState.replayFrames;
            if (frames.Count == 1)
                return frames[0];

            var rightIndex = frames.FindIndex(frame => frame.SampleTime >= sampleTime);
            if (rightIndex <= 0)
                return frames[0];
            if (rightIndex == -1)
                return frames[^1];

            var left = frames[rightIndex - 1];
            var right = frames[rightIndex];
            var duration = Math.Max(0.0001d, right.SampleTime - left.SampleTime);
            var alpha = Math.Clamp((sampleTime - left.SampleTime) / duration, 0d, 1d);

            if (left.TeleportCut || right.TeleportCut || left.Position == null || right.Position == null || left.Rotation == null || right.Rotation == null || left.Speed == null || right.Speed == null)
                return alpha < 0.5d ? left : right;

            return new PlayerReplays.ReplayFrames
            {
                SampleIndex = alpha < 0.5d ? left.SampleIndex : right.SampleIndex,
                SampleTime = sampleTime,
                Position = new ReplayVector(
                    LerpFloat(left.Position.X, right.Position.X, alpha),
                    LerpFloat(left.Position.Y, right.Position.Y, alpha),
                    LerpFloat(left.Position.Z, right.Position.Z, alpha)
                ),
                Rotation = new ReplayQAngle(
                    LerpAngle(left.Rotation.Pitch, right.Rotation.Pitch, alpha),
                    LerpAngle(left.Rotation.Yaw, right.Rotation.Yaw, alpha),
                    LerpAngle(left.Rotation.Roll, right.Rotation.Roll, alpha)
                ),
                Speed = new ReplayVector(
                    LerpFloat(left.Speed.X, right.Speed.X, alpha),
                    LerpFloat(left.Speed.Y, right.Speed.Y, alpha),
                    LerpFloat(left.Speed.Z, right.Speed.Z, alpha)
                ),
                Buttons = alpha < 0.5d ? left.Buttons : right.Buttons,
                Flags = alpha < 0.5d ? left.Flags : right.Flags,
                MoveType = alpha < 0.5d ? left.MoveType : right.MoveType,
                DuckAmount = LerpFloat(left.DuckAmount, right.DuckAmount, alpha),
                Grounded = alpha < 0.5d ? left.Grounded : right.Grounded,
                TimerTicks = alpha < 0.5d ? left.TimerTicks : right.TimerTicks,
                TeleportCut = false
            };
        }

        private void ReplayPlaybackInterpolated(CCSPlayerController player, PlayerReplays replayState, double sampleTime)
        {
            if (!IsAllowedPlayer(player))
                return;

            if (!playerTimers.TryGetValue(player.Slot, out PlayerTimerInfo? value))
                return;

            var replayFrame = SampleReplayFrame(replayState, sampleTime);
            replayState.CurrentReplayFrame = replayFrame;

            if (player.IsBot && replayState.UseDetachedReplayView)
                ClearDetachedReplayView(player, replayState);

            if (replayState.UseDetachedReplayView && replayState.ReplayCamera != null && replayState.ReplayCamera.IsValid)
            {
                UpdateDetachedReplayView(player, replayState, replayFrame);
                PrintReplayButtons(player, replayFrame, value);
                return;
            }

            if (replayFrame.Grounded)
                SetMoveType(player, MoveType_t.MOVETYPE_WALK);
            else
                SetMoveType(player, MoveType_t.MOVETYPE_OBSERVER);

            value.MovementService!.DuckAmount = replayFrame.DuckAmount;
            var replayRotation = GetReplayPawnRotation(player, replayFrame.Rotation!);
            player.PlayerPawn.Value!.Teleport(
                ReplayVector.ToVector(replayFrame.Position!),
                ReplayQAngle.ToQAngle(replayRotation),
                ReplayVector.ToVector(replayFrame.Speed!)
            );
            EmitReplayBotTrail(player, replayState, replayFrame.Position, replayFrame.TeleportCut);

            PrintReplayButtons(player, replayFrame, value);
        }

        private CBaseEntity? GetReplayViewTargetEntity()
        {
            if (replayBotController != null && replayBotController.IsValid && replayBotController.PlayerPawn?.Value != null)
                return replayBotController.PlayerPawn.Value;

            if (replayBotVisualEntity != null && replayBotVisualEntity.IsValid)
                return replayBotVisualEntity;

            return null;
        }

        private bool TryGetReplayV2Root(JsonElement root, out JsonElement versionElement)
        {
            versionElement = default;

            if (root.ValueKind != JsonValueKind.Object)
                return false;

            bool hasVersion = root.TryGetProperty("Version", out versionElement) ||
                              root.TryGetProperty("version", out versionElement);
            if (!hasVersion || versionElement.ValueKind != JsonValueKind.Number || versionElement.GetInt32() < 2)
                return false;

            return root.TryGetProperty("Frames", out _) || root.TryGetProperty("frames", out _);
        }

        private async Task<PlayerReplays?> LoadReplayStateFromJson(string steamId, int bonusX = 0, int style = 0, string mode = "", bool useInterpolatedPlayback = false)
        {
            string fileName = $"{steamId}_replay.json";
            string playerReplaysPath;
            if (style != 0) playerReplaysPath = Path.Join(gameDir, "csgo", "cfg", "SharpTimer", "PlayerReplayData", bonusX == 0 ? currentMapName : $"{currentMapName}_bonus{bonusX}", GetNamedStyle(style), mode, fileName);
            else playerReplaysPath = Path.Join(gameDir, "csgo", "cfg", "SharpTimer", "PlayerReplayData", bonusX == 0 ? currentMapName : $"{currentMapName}_bonus{bonusX}", mode, fileName);

            if (!File.Exists(playerReplaysPath))
            {
                Utils.LogError($"File does not exist: {playerReplaysPath}");
                return null;
            }

            try
            {
                var jsonString = await File.ReadAllTextAsync(playerReplaysPath);
                if (jsonString.Contains("PositionString"))
                    return null;

                using var document = JsonDocument.Parse(jsonString);
                if (TryGetReplayV2Root(document.RootElement, out _))
                {
                    var replayFile = JsonSerializer.Deserialize<ReplayFileV2>(jsonString);
                    if (replayFile == null)
                        return null;

                    return new PlayerReplays
                    {
                        replayFrames = replayFile.Frames ?? [],
                        RecordingTickrate = replayFile.Tickrate > 0 ? replayFile.Tickrate : 64,
                        PlaybackTimeSeconds = 0,
                        UseInterpolatedPlayback = useInterpolatedPlayback
                    };
                }

                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var indexedReplayFrames = JsonSerializer.Deserialize<List<IndexedReplayFrames>>(jsonString);
                    if (indexedReplayFrames == null)
                        return null;

                    return new PlayerReplays
                    {
                        replayFrames = indexedReplayFrames.OrderBy(frame => frame.Index).Select(frame => frame.Frame!).ToList(),
                        RecordingTickrate = 64,
                        PlaybackTimeSeconds = 0,
                        UseInterpolatedPlayback = false
                    };
                }
            }
            catch (Exception ex)
            {
                Utils.LogError($"Error during deserialization: {ex.Message}");
            }

            return null;
        }

        private void ClearReplayBotVisualEntity()
        {
            try
            {
                if (replayBotVisualEntity != null && replayBotVisualEntity.IsValid)
                    replayBotVisualEntity.Remove();
            }
            catch
            {
            }

            replayBotVisualEntity = null;
            replayBotVisualReplay = new PlayerReplays();
        }

        private bool StartReplayVisualEntity(PlayerReplays replayState, string replayHolderName)
        {
            ClearReplayBotVisualEntity();
            ClearReplayBotFallbackSpawns();

            var entity = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
            if (entity == null)
            {
                Utils.LogError("Failed to create replay visual entity");
                return false;
            }

            entity.DispatchSpawn();
            entity.SetModel(replayBotVisualModel);

            var firstFrame = replayState.replayFrames.FirstOrDefault(frame => frame.Position != null && frame.Rotation != null);
            if (firstFrame?.Position != null && firstFrame.Rotation != null)
            {
                entity.Teleport(
                    ReplayVector.ToVector(firstFrame.Position),
                    ReplayQAngle.ToQAngle(GetReplayDisplayRotation(firstFrame.Rotation)),
                    ReplayVector.ToVector(new ReplayVector(0, 0, 0))
                );
            }
            else if (currentRespawnPos.HasValue)
            {
                var respawnPos = new Vector(currentRespawnPos.Value.X, currentRespawnPos.Value.Y, currentRespawnPos.Value.Z);
                var respawnAng = currentRespawnAng.HasValue
                    ? new QAngle(currentRespawnAng.Value.X, currentRespawnAng.Value.Y, currentRespawnAng.Value.Z)
                    : new QAngle(0, 0, 0);
                entity.Teleport(respawnPos, respawnAng, new Vector(0, 0, 0));
            }

            entity.Collision.CollisionAttribute.CollisionGroup = (byte)CollisionGroup.COLLISION_GROUP_DISSOLVING;
            entity.Collision.CollisionGroup = (byte)CollisionGroup.COLLISION_GROUP_DISSOLVING;
            Utilities.SetStateChanged(entity, "CCollisionProperty", "m_CollisionGroup");
            Utilities.SetStateChanged(entity, "CCollisionProperty", "m_collisionAttribute");

            replayBotName = $"[SR] {replayHolderName}";
            replayBotVisualEntity = entity;
            replayBotVisualReplay = replayState;
            replayBotVisualReplay.CurrentPlaybackFrame = 0;
            replayBotVisualReplay.PlaybackTimeSeconds = 0;
            replayBotVisualReplay.LastTrailPosition = null;
            replayBotVisualReplay.LastTrailEmitAtMilliseconds = 0;
            replayBotSpawnPending = false;
            Utils.LogDebug($"Started replay visual entity for {replayBotName}");
            return true;
        }

        private async Task<bool> StartReplayVisualEntityFallback()
        {
            var (srSteamID, srPlayerName, _) = enableDb ? await GetMapRecordSteamIDFromDatabase() : await GetMapRecordSteamID();
            var replayState = await LoadReplayStateFromJson(srSteamID, 0, 0, GetModeName(defaultMode), true);
            if (replayState == null)
                return false;

            var completion = new TaskCompletionSource<bool>();
            Server.NextFrame(() =>
            {
                try
                {
                    completion.TrySetResult(StartReplayVisualEntity(replayState, srPlayerName));
                }
                catch
                {
                    completion.TrySetResult(false);
                }
            });

            return await completion.Task;
        }

        private void ReplayVisualEntityOnTick()
        {
            try
            {
                if (replayBotVisualEntity == null || !replayBotVisualEntity.IsValid)
                    return;

                var replayState = replayBotVisualReplay;
                int totalFrames = replayState.replayFrames.Count;
                if (totalFrames == 0)
                    return;

                if (ShouldUseInterpolatedReplay(replayState))
                {
                    var tickrate = replayState.RecordingTickrate > 0 ? replayState.RecordingTickrate : 64;
                    var frameStep = 1.0d / tickrate;
                    var maxPlaybackTime = replayState.replayFrames[^1].SampleTime;

                    if (replayState.PlaybackTimeSeconds < 0 || replayState.PlaybackTimeSeconds > maxPlaybackTime)
                    {
                        replayState.PlaybackTimeSeconds = 0;
                        replayState.CurrentPlaybackFrame = 0;
                        replayState.LastTrailPosition = null;
                        replayState.LastTrailEmitAtMilliseconds = 0;
                    }

                    var sampleTime = Math.Min(replayState.PlaybackTimeSeconds + (frameStep * 0.5d), maxPlaybackTime);
                    var replayFrame = SampleReplayFrame(replayState, sampleTime);
                    replayBotVisualEntity.Teleport(
                        ReplayVector.ToVector(replayFrame.Position!),
                        ReplayQAngle.ToQAngle(GetReplayDisplayRotation(replayFrame.Rotation!)),
                        ReplayVector.ToVector(replayFrame.Speed!)
                    );
                    EmitReplayBotTrail(null, replayState, replayFrame.Position, replayFrame.TeleportCut);
                    replayState.CurrentPlaybackFrame = Math.Min((int)Math.Floor(sampleTime * tickrate), totalFrames - 1);
                    replayState.PlaybackTimeSeconds += frameStep;

                    if (replayState.PlaybackTimeSeconds > maxPlaybackTime + frameStep)
                    {
                        replayState.PlaybackTimeSeconds = 0;
                        replayState.CurrentPlaybackFrame = 0;
                        replayState.LastTrailPosition = null;
                        replayState.LastTrailEmitAtMilliseconds = 0;
                    }

                    return;
                }

                if (replayState.CurrentPlaybackFrame < 0 || replayState.CurrentPlaybackFrame >= totalFrames)
                {
                    replayState.CurrentPlaybackFrame = 0;
                    replayState.LastTrailPosition = null;
                    replayState.LastTrailEmitAtMilliseconds = 0;
                }

                var frame = replayState.replayFrames[replayState.CurrentPlaybackFrame];
                replayBotVisualEntity.Teleport(
                    ReplayVector.ToVector(frame.Position!),
                    ReplayQAngle.ToQAngle(GetReplayDisplayRotation(frame.Rotation!)),
                    ReplayVector.ToVector(frame.Speed!)
                );
                EmitReplayBotTrail(null, replayState, frame.Position, frame.TeleportCut);
                replayState.CurrentPlaybackFrame++;
            }
            catch (Exception ex)
            {
                Utils.LogError($"Error in ReplayVisualEntityOnTick: {ex.Message}");
            }
        }

        private void ReplayPlay(CCSPlayerController player)
        {
            try
            {
                var replayState = playerReplays[player.Slot];
                int totalFrames = replayState.replayFrames.Count;

                if (totalFrames <= 128 && replayBotController?.Handle != player.Handle)
                {
                    OnRecordingStop(player);
                }

                if (ShouldUseInterpolatedReplay(replayState))
                {
                    var tickrate = replayState.RecordingTickrate > 0 ? replayState.RecordingTickrate : 64;
                    var frameStep = 1.0d / tickrate;
                    var maxPlaybackTime = replayState.replayFrames[^1].SampleTime;

                    if (replayState.PlaybackTimeSeconds < 0 || replayState.PlaybackTimeSeconds > maxPlaybackTime)
                    {
                        replayState.PlaybackTimeSeconds = 0;
                        replayState.CurrentPlaybackFrame = 0;
                        replayState.LastTrailPosition = null;
                        replayState.LastTrailEmitAtMilliseconds = 0;
                        Action<CCSPlayerController?, float, bool> adjustVelocity = use2DSpeed ? AdjustPlayerVelocity2D : AdjustPlayerVelocity;
                        adjustVelocity(player, 0, false);
                    }

                    var sampleTime = Math.Min(replayState.PlaybackTimeSeconds + (frameStep * 0.5d), maxPlaybackTime);
                    ReplayPlaybackInterpolated(player, replayState, sampleTime);
                    replayState.CurrentPlaybackFrame = Math.Min((int)Math.Floor(sampleTime * tickrate), totalFrames - 1);
                    replayState.PlaybackTimeSeconds += frameStep;

                    if (replayState.PlaybackTimeSeconds > maxPlaybackTime + frameStep)
                    {
                        replayState.PlaybackTimeSeconds = 0;
                        replayState.CurrentPlaybackFrame = 0;
                        replayState.LastTrailPosition = null;
                        replayState.LastTrailEmitAtMilliseconds = 0;
                    }

                    return;
                }

                if (replayState.CurrentPlaybackFrame < 0 || replayState.CurrentPlaybackFrame >= totalFrames)
                {
                    replayState.CurrentPlaybackFrame = 0;
                    replayState.LastTrailPosition = null;
                    replayState.LastTrailEmitAtMilliseconds = 0;
                    Action<CCSPlayerController?, float, bool> adjustVelocity = use2DSpeed ? AdjustPlayerVelocity2D : AdjustPlayerVelocity;
                    adjustVelocity(player, 0, false);
                }

                ReplayPlayback(player, replayState.CurrentPlaybackFrame);
                replayState.CurrentPlaybackFrame++;
            }
            catch (Exception ex)
            {
                Utils.LogError($"Error in ReplayPlay: {ex.Message}");
            }
        }

        private void OnRecordingStart(CCSPlayerController player, int bonusX = 0, int style = 0)
        {
            try
            {
                playerReplays.Remove(player.Slot);
                playerReplays[player.Slot] = new PlayerReplays
                {
                    BonusX = bonusX,
                    Style = style,
                    RecordingTickrate = 64,
                    PlaybackTimeSeconds = 0,
                    UseInterpolatedPlayback = false
                };
                playerTimers[player.Slot].IsRecordingReplay = true;
            }
            catch (Exception ex)
            {
                Utils.LogError($"Error in OnRecordingStart: {ex.Message}");
            }
        }

        private void OnRecordingStop(CCSPlayerController player)
        {
            try
            {
                playerTimers[player.Slot].IsRecordingReplay = false;
                SetMoveType(player, MoveType_t.MOVETYPE_WALK);
            }
            catch (Exception ex)
            {
                Utils.LogError($"Error in OnRecordingStop: {ex.Message}");
            }
        }

        public async Task DumpReplayToJson(CCSPlayerController player, string steamID, int slot, int bonusX = 0,
            int style = 0, string mode = "")
        {
            await Task.Run(() =>
            {
                if (!IsAllowedPlayer(player))
                {
                    Utils.LogError($"Error in DumpReplayToJson: Player not allowed or not on server anymore");
                    return;
                }

                string fileName = $"{steamID}_replay.json";
                string playerReplaysDirectory;
                playerReplaysDirectory = Path.Join(gameDir, "csgo", "cfg", "SharpTimer", "PlayerReplayData",
                    bonusX == 0 ? $"{currentMapName}" : $"{currentMapName}_bonus{bonusX}", GetNamedStyle(style), mode);
                string playerReplaysPath = Path.Join(playerReplaysDirectory, fileName);

                try
                {
                    if (!Directory.Exists(playerReplaysDirectory))
                    {
                        Directory.CreateDirectory(playerReplaysDirectory);
                    }

                    if (playerReplays[slot].replayFrames.Count >= maxReplayFrames) return;

                    var replayFile = new ReplayFileV2
                    {
                        Version = 2,
                        MapName = bonusX == 0 ? currentMapName! : $"{currentMapName}_bonus{bonusX}",
                        BonusX = bonusX,
                        Style = style,
                        Tickrate = playerReplays[slot].RecordingTickrate,
                        TotalFrames = playerReplays[slot].replayFrames.Count,
                        Frames = playerReplays[slot].replayFrames
                    };

                    using (Stream stream = new FileStream(playerReplaysPath, FileMode.Create))
                    {
                        JsonSerializer.Serialize(stream, replayFile);
                    }
                }
                catch (Exception ex)
                {
                    Utils.LogError($"Error during serialization: {ex.Message}");
                }
            });
        }

        public async Task DumpReplayToBinary(CCSPlayerController player, string steamID, int playerSlot, int bonusX = 0,
            int style = 0, string mode = "")
        {
            await Task.Run(() =>
            {
                if (!IsAllowedPlayer(player))
                {
                    Utils.LogError($"Error in DumpReplayToBinary: Player not allowed or not on server anymore");
                    return;
                }

                string fileName = $"{steamID}_replay.dat";
                string playerReplaysDirectory;
                playerReplaysDirectory = Path.Join(gameDir, "csgo", "cfg", "SharpTimer", "PlayerReplayData",
                    bonusX == 0 ? $"{currentMapName}" : $"{currentMapName}_bonus{bonusX}", GetNamedStyle(style), mode);
                string playerReplaysPath = Path.Join(playerReplaysDirectory, fileName);

                try
                {
                    if (!Directory.Exists(playerReplaysDirectory))
                    {
                        Directory.CreateDirectory(playerReplaysDirectory);
                    }

                    if (playerReplays[playerSlot].replayFrames.Count >= maxReplayFrames) return;

                    var indexedReplayFrames = playerReplays[playerSlot].replayFrames
                        .Select((frame, index) => new IndexedReplayFrames { Index = index, Frame = frame })
                        .ToList();

                    using Stream stream = new FileStream(playerReplaysPath, FileMode.Create);
                    BinaryWriter writer = new BinaryWriter(stream);

                    writer.Write(REPLAY_VERSION);

                    foreach (var frame in indexedReplayFrames)
                    {
                        writer.Write(frame.Frame!.Position!.X);
                        writer.Write(frame.Frame.Position!.Y);
                        writer.Write(frame.Frame.Position!.Z);
                        writer.Write(frame.Frame.Rotation!.Pitch);
                        writer.Write(frame.Frame.Rotation!.Yaw);
                        writer.Write(frame.Frame.Rotation!.Roll);
                        writer.Write(frame.Frame.Speed!.X);
                        writer.Write(frame.Frame.Speed!.Y);
                        writer.Write(frame.Frame.Speed!.Z);
                        writer.Write((int)frame.Frame!.Buttons!);
                        writer.Write((int)frame.Frame.Flags);
                        writer.Write((int)frame.Frame.MoveType);
                    }
                }
                catch (Exception ex)
                {
                    Utils.LogError($"Error during serialization: {ex.Message}");
                }
            });
        }

        public string SerializeFrameToBinaryString(List<IndexedReplayFrames> frames)
        {
            using Stream stream = new MemoryStream();
            BinaryWriter writer = new BinaryWriter(stream);

            writer.Write(REPLAY_VERSION);

            foreach (var frame in frames)
            {
                writer.Write(frame.Frame!.Position!.X);
                writer.Write(frame.Frame.Position!.Y);
                writer.Write(frame.Frame.Position!.Z);
                writer.Write(frame.Frame.Rotation!.Pitch);
                writer.Write(frame.Frame.Rotation!.Yaw);
                writer.Write(frame.Frame.Rotation!.Roll);
                writer.Write(frame.Frame.Speed!.X);
                writer.Write(frame.Frame.Speed!.Y);
                writer.Write(frame.Frame.Speed!.Z);
                writer.Write((int)frame.Frame!.Buttons!);
                writer.Write((int)frame.Frame.Flags);
                writer.Write((int)frame.Frame.MoveType);
            }

            var memoryStream = (MemoryStream)stream;
            return Convert.ToBase64String(memoryStream.ToArray());
        }

        public string GetReplayBinary(CCSPlayerController player, int slot)
        {
            if (!IsAllowedPlayer(player))
            {
                Utils.LogError($"Error in GetReplayJson: Player not allowed or not on server anymore");
                return "";
            }

            try
            {
                if (playerReplays[slot].replayFrames.Count >= maxReplayFrames) return "";

                var indexedReplayFrames = playerReplays[slot].replayFrames
                    .Select((frame, index) => new IndexedReplayFrames { Index = index, Frame = frame })
                    .ToList();

                return SerializeFrameToBinaryString(indexedReplayFrames);
            }
            catch (Exception ex)
            {
                Utils.LogError($"Error during serialization: {ex.Message}");
                return "";
            }
        }

        private async Task ReadReplayFromJson(CCSPlayerController player, string steamId, int slot, int bonusX = 0, int style = 0, string mode = "", bool useInterpolatedPlayback = false)
        {
            var replayState = await LoadReplayStateFromJson(steamId, bonusX, style, mode, useInterpolatedPlayback);
            if (replayState == null)
            {
                Server.NextFrame(() => Utils.PrintToChat(player, Localizer["replay_dont_exist"]));
                return;
            }

            playerReplays[slot] = replayState;
        }

        private async Task ReadReplayFromBinary(CCSPlayerController player, string steamId, int playerSlot,
            int bonusX = 0, int style = 0, string mode = "")
        {
            string fileName = $"{steamId}_replay.dat";
            string playerReplaysPath;
            playerReplaysPath = Path.Join(gameDir, "csgo", "cfg", "SharpTimer", "PlayerReplayData",
                bonusX == 0 ? currentMapName : $"{currentMapName}_bonus{bonusX}", GetNamedStyle(style), mode, fileName);

            try
            {
                if (!File.Exists(playerReplaysPath))
                {
                    Utils.LogError($"File does not exist: {playerReplaysPath}");
                    Server.NextFrame(() => Utils.PrintToChat(player, Localizer["replay_dont_exist"]));
                    return;
                }

                using Stream stream = new FileStream(playerReplaysPath, FileMode.Open);
                BinaryReader reader = new BinaryReader(stream);

                var version = reader.ReadInt32();
                if (version != REPLAY_VERSION)
                {
                    Utils.LogError($"Unsupported replay version: {version}");
                    Server.NextFrame(() => Utils.PrintToChat(player, $"Unsupported replay version: {version}"));
                    return;
                }

                var replayFrames = new List<PlayerReplays.ReplayFrames>();
                await Server.NextFrameAsync(() =>
                {
                    while (reader.BaseStream.Position != reader.BaseStream.Length)
                    {
                        var position = new Vector(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        var rotation = new QAngle(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        var speed = new Vector(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        var buttons = (PlayerButtons)reader.ReadInt32();
                        var flags = (uint)reader.ReadInt32();
                        var moveType = (MoveType_t)reader.ReadInt32();

                        replayFrames.Add(new PlayerReplays.ReplayFrames
                        {
                            Position = ReplayVector.GetVectorish(position),
                            Rotation = ReplayQAngle.GetQAngleish(rotation),
                            Speed = ReplayVector.GetVectorish(speed),
                            Buttons = buttons,
                            Flags = flags,
                            MoveType = moveType
                        });
                    }
                });

                if (!playerReplays.TryGetValue(playerSlot, out PlayerReplays? value))
                {
                    value = new PlayerReplays();
                    playerReplays[playerSlot] = value;
                }

                value.replayFrames = replayFrames;
            }
            catch (Exception ex)
            {
                Utils.LogError($"Error during deserialization: {ex.Message}");
            }
        }

        private async Task ReadReplayFromGlobal(CCSPlayerController player, int recordId, string mode, int bonusX = 0)
        {
            var payload = new
            {
                record_id = recordId,
                map_id = mapCache.MapID,
                mode,
                bonus = bonusX
            };

            try
            {
                byte[] replayData = Convert.FromBase64String(await GetReplayFromGlobal(payload));
                using Stream stream = new MemoryStream(replayData);
                using BinaryReader reader = new BinaryReader(stream);

                var version = reader.ReadInt32();
                if (version != REPLAY_VERSION)
                {
                    Utils.LogError($"Unsupported replay version: {version}");
                    Server.NextFrame(() => Utils.PrintToChat(player, $"Unsupported replay version: {version}"));
                    return;
                }

                var replayFrames = new List<PlayerReplays.ReplayFrames>();
                await Server.NextFrameAsync(() =>
                {
                    while (reader.BaseStream.Position != reader.BaseStream.Length)
                    {
                        var position = new Vector(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        var rotation = new QAngle(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        var speed = new Vector(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        var buttons = (PlayerButtons)reader.ReadInt32();
                        var flags = (uint)reader.ReadInt32();
                        var moveType = (MoveType_t)reader.ReadInt32();

                        replayFrames.Add(new PlayerReplays.ReplayFrames
                        {
                            Position = ReplayVector.GetVectorish(position),
                            Rotation = ReplayQAngle.GetQAngleish(rotation),
                            Speed = ReplayVector.GetVectorish(speed),
                            Buttons = buttons,
                            Flags = flags,
                            MoveType = moveType
                        });
                    }
                });

                if (!playerReplays.TryGetValue(player.Slot, out PlayerReplays? value))
                {
                    value = new PlayerReplays();
                    playerReplays[player.Slot] = value;
                }

                value.replayFrames = replayFrames!;
            }
            catch (Exception ex)
            {
                Utils.LogError($"Error during deserialization: {ex.Message}");
            }
        }

        private void ClearReplayBotFallbackSpawns()
        {
            foreach (var spawn in replayBotFallbackSpawns)
            {
                try
                {
                    if (spawn != null && spawn.IsValid)
                    {
                        spawn.Remove();
                    }
                }
                catch
                {
                }
            }

            replayBotFallbackSpawns.Clear();
        }

        private void CreateReplayBotFallbackSpawn(string className, Vector_t position, QAngle_t angle)
        {
            var spawn = Utilities.CreateEntityByName<CBaseEntity>(className);
            if (spawn == null)
            {
                Utils.LogError($"Failed to create replay bot fallback spawn entity {className}");
                return;
            }

            spawn.Teleport(position, angle, new Vector_t(0, 0, 0));
            spawn.DispatchSpawn();
            replayBotFallbackSpawns.Add(spawn);
            Utils.LogDebug($"Created replay bot fallback spawn {className} at {position}");
        }

        private void EnsureReplayBotFallbackSpawns()
        {
            if (replayBotFallbackSpawns.Any(spawn => spawn != null && spawn.IsValid))
                return;

            if (!currentRespawnPos.HasValue)
            {
                Utils.LogDebug("Replay bot fallback spawn skipped: currentRespawnPos unavailable");
                return;
            }

            var basePosition = currentRespawnPos.Value;
            var baseAngle = currentRespawnAng ?? new QAngle_t(0, 0, 0);
            var offsets = new[]
            {
                new Vector_t(0, 0, 0),
                new Vector_t(32, 0, 0),
                new Vector_t(-32, 0, 0),
                new Vector_t(0, 32, 0),
                new Vector_t(0, -32, 0),
            };

            foreach (var offset in offsets)
            {
                var position = new Vector_t(
                    basePosition.X + offset.X,
                    basePosition.Y + offset.Y,
                    basePosition.Z + offset.Z
                );

                CreateReplayBotFallbackSpawn("info_player_counterterrorist", position, baseAngle);
                CreateReplayBotFallbackSpawn("info_player_terrorist", position, baseAngle);
            }
        }

        private void FinalizeReplayBotSpawn(int attemptsRemaining)
        {
            var bot = Utilities.GetPlayers().Where(b => b.IsBot && !b.IsHLTV).FirstOrDefault();
            if (bot == null)
            {
                if (attemptsRemaining > 0)
                {
                    Utils.LogDebug($"Replay bot not visible yet, retrying spawn search ({attemptsRemaining})...");
                    AddTimer(0.25f, () => FinalizeReplayBotSpawn(attemptsRemaining - 1), TimerFlags.STOP_ON_MAPCHANGE);
                }
                else
                {
                    _ = Task.Run(async () =>
                    {
                        if (await StartReplayVisualEntityFallback())
                            return;

                        replayBotSpawnPending = false;
                        Utils.LogError($"Failed to spawn replay bot");
                    });
                }
                return;
            }

            replayBotController = bot;
            Utils.LogDebug($"Found replay bot: {bot.PlayerName}");
            ClearReplayBotFallbackSpawns();

            var botPawn = bot.PlayerPawn.Value;
            if (botPawn == null)
            {
                if (attemptsRemaining > 0)
                {
                    Utils.LogDebug($"Replay bot pawn not ready yet, retrying spawn finalize ({attemptsRemaining})...");
                    AddTimer(0.25f, () => FinalizeReplayBotSpawn(attemptsRemaining - 1), TimerFlags.STOP_ON_MAPCHANGE);
                }
                else
                {
                    replayBotSpawnPending = false;
                    Utils.LogError($"Failed to spawn replay bot");
                }
                return;
            }

            bot.RemoveWeapons();
            botPawn.Bot!.IsStopping = true;
            botPawn.Bot.IsSleeping = true;
            botPawn.Bot.AllowActive = true;
            botPawn.Collision.CollisionAttribute.CollisionGroup = (byte)CollisionGroup.COLLISION_GROUP_DISSOLVING;
            botPawn.Collision.CollisionGroup = (byte)CollisionGroup.COLLISION_GROUP_DISSOLVING;
            Utilities.SetStateChanged(bot, "CCollisionProperty", "m_CollisionGroup");
            Utilities.SetStateChanged(bot, "CCollisionProperty", "m_collisionAttribute");
            Utils.LogDebug($"Configured replay bot collision and weapons for {bot.PlayerName}");

            OnPlayerConnect(bot, true);
            ChangePlayerName(bot, replayBotName);
            playerTimers[bot.Slot].IsTimerBlocked = true;
            _ = Task.Run(async () => await ReplayHandler(bot, bot.Slot));
            replayBotSpawnPending = false;
            Utils.LogDebug($"Starting replay for {bot.PlayerName}");

            var bots = Utilities.GetPlayers().Where(b => b.IsBot && !b.IsHLTV && b != replayBotController);
            foreach (var kicked in bots)
            {
                OnPlayerDisconnect(kicked, true);
                Server.ExecuteCommand("bot_quota 1");
                Server.ExecuteCommand($"kickid {kicked.UserId}");
                Server.ExecuteCommand("bot_quota 1");
                Utils.LogDebug($"Kicking unused bot on spawn... {kicked.PlayerName}");
            }
        }

        private async Task SpawnReplayBot()
        {
            if (replayBotSpawnPending)
                return;

            replayBotSpawnPending = true;

            if (!await CheckSRReplay("x", 0, 0, GetModeName(defaultMode)))
            {
                replayBotSpawnPending = false;
                Utils.LogError("Replay check failed, not spawning bot.");
                return;
            }

            Server.NextFrame(() =>
            {
                AddTimer(3.0f, () =>
                {
                    Server.ExecuteCommand("bot_quota_mode normal");
                    Server.ExecuteCommand("bot_quota 0");
                    Server.ExecuteCommand("bot_chatter off");
                    Server.ExecuteCommand("bot_controllable 0");
                    Server.ExecuteCommand("bot_kick");
                    replayBotController = null;

                    AddTimer(3.0f, () =>
                    {
                        EnsureReplayBotFallbackSpawns();
                        Server.ExecuteCommand("bot_quota 1");
                        Server.ExecuteCommand("bot_add_ct");
                        Server.ExecuteCommand("bot_add_t");
                        Server.ExecuteCommand("bot_add");
                        Server.ExecuteCommand("bot_quota 1");

                        Utils.LogDebug("Searching for replay bot...");
                        AddTimer(0.5f, () => FinalizeReplayBotSpawn(16), TimerFlags.STOP_ON_MAPCHANGE);
                    }, TimerFlags.STOP_ON_MAPCHANGE);
                }, TimerFlags.STOP_ON_MAPCHANGE);
            });
        }

        public async Task<bool> CheckSRReplay(string topSteamID = "x", int bonusX = 0, int style = 0, string mode = "")
        {
            var (srSteamID, srPlayerName, srTime) = ("null", "null", 0);

            if (topSteamID == "x")
            {
                if (enableDb)
                    (srSteamID, srPlayerName, srTime) = await GetMapRecordSteamIDFromDatabase(bonusX, 0, style, mode);
                else
                    (srSteamID, srPlayerName, srTime) = await GetMapRecordSteamID(bonusX);
            }
            
            string ext = useBinaryReplays ? "dat" : "json";
            string fileName = $"{(topSteamID == "x" ? $"{srSteamID}" : $"{topSteamID}")}_replay.{ext}";
            string playerReplaysPath;
            playerReplaysPath = Path.Join(gameDir, "csgo", "cfg", "SharpTimer", "PlayerReplayData",
                (bonusX == 0 ? currentMapName : $"{currentMapName}_bonus{bonusX}"), GetNamedStyle(style), mode,
                fileName);
            try
            {
                if (File.Exists(playerReplaysPath))
                {
                    if (useBinaryReplays)
                    {
                        using var reader = new BinaryReader(File.Open(playerReplaysPath, FileMode.Open));
                        var version = reader.ReadInt32();
                        return version == REPLAY_VERSION;
                    }

                    var jsonString = await File.ReadAllTextAsync(playerReplaysPath);
                    if (!jsonString.Contains("PositionString"))
                    {
                        using var document = JsonDocument.Parse(jsonString);
                        if (document.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            var indexedReplayFrames = JsonSerializer.Deserialize<List<IndexedReplayFrames>>(jsonString);
                            return indexedReplayFrames != null;
                        }

                        if (document.RootElement.ValueKind == JsonValueKind.Object &&
                            TryGetReplayV2Root(document.RootElement, out _))
                        {
                            var replayFile = JsonSerializer.Deserialize<ReplayFileV2>(jsonString);
                            return replayFile?.Frames != null && replayFile.Frames.Count > 0;
                        }

                        return false;
                    }
                    else
                    {
                        return false;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during deserialization: {ex.Message}");
                return false;
            }
        }
    }
}

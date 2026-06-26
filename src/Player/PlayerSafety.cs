using CounterStrikeSharp.API.Core;

namespace SharpTimer
{
    public partial class SharpTimer
    {
        public void StopTimerForUtility(CCSPlayerController? player, bool blockTimer = false, bool clearCheckpoints = true)
        {
            if (player == null || !player.IsValid || !playerTimers.TryGetValue(player.Slot, out PlayerTimerInfo? playerTimer))
                return;

            int slot = player.Slot;

            if (clearCheckpoints)
            {
                playerCheckpoints.Remove(slot);
                playerTimer.CheckpointIndex = 0;
            }

            playerTimer.IsRecordingReplay = false;
            playerTimer.IsTimerRunning = false;
            playerTimer.TimerTicks = 0;
            playerTimer.StageTicks = 0;
            playerTimer.IsBonusTimerRunning = false;
            playerTimer.BonusTimerTicks = 0;
            playerTimer.CurrentMapCheckpoint = 0;
            playerTimer.CurrentMapStage = 0;
            playerTimer.IsTimerBlocked = blockTimer;

            if (stageTriggerCount != 0 || cpTriggerCount != 0)
            {
                playerTimer.StageTimes?.Clear();
                playerTimer.StageVelos?.Clear();
            }
        }
    }
}

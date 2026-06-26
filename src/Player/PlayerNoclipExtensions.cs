using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace SharpTimer
{
    public static class PlayerNoclipExtensions
    {
        public static bool IsPlayerNoclipping(this CCSPlayerController? player)
        {
            try
            {
                var pawn = player?.PlayerPawn?.Value;
                if (pawn == null)
                    return false;

                return pawn.MoveType.HasFlag(MoveType_t.MOVETYPE_NOCLIP)
                    || pawn.ActualMoveType.HasFlag(MoveType_t.MOVETYPE_NOCLIP);
            }
            catch
            {
                return false;
            }
        }
    }
}

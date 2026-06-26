using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace SharpTimer
{
    public partial class SharpTimer
    {
        // Issue #2: bring a player stuck on the Spectator team back into play so the
        // utility commands (!r / !spec) can rescue them. Mirrors the team dance the
        // upstream css_spec come-back uses. Returns true if a rejoin was performed.
        public bool RejoinFromSpectator(CCSPlayerController? player)
        {
            if (player == null || !player.IsValid || player.Team != CsTeam.Spectator)
                return false;

            player.ChangeTeam(CsTeam.CounterTerrorist);
            player.Respawn();
            player.CommitSuicide(false, true);
            player.ChangeTeam(CsTeam.CounterTerrorist);
            return true;
        }
    }
}

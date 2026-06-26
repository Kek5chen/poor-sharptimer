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

using System.Reflection;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;

namespace SharpTimer
{
    public class RemoveDamage
    {
        private readonly SharpTimer Plugin;
        private readonly Utils Utils;
        private object? LinuxDamageHook;

        public RemoveDamage(SharpTimer plugin)
        {
            Plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            Utils = plugin.Utils ?? throw new ArgumentNullException(nameof(plugin.Utils));
        }

        public void Hook()
        {
            Utils.LogDebug("Hook RemoveDamage");

            try
            {
                if (Plugin.isLinux)
                {
                    Utils.LogDebug("Trying to register Linux Damage hook...");
                    FieldInfo? field = typeof(VirtualFunctions).GetField("CBaseEntity_TakeDamageOldFunc", BindingFlags.Public | BindingFlags.Static);
                    if (field == null)
                    {
                        Utils.LogError("Error in DamageHook: CBaseEntity_TakeDamageOldFunc is unavailable in this CounterStrikeSharp build");
                        return;
                    }

                    LinuxDamageHook = field.GetValue(null);
                    InvokeLinuxDamageHook("Hook");
                }
                else
                {
                    Utils.LogDebug("Trying to register Windows Damage hook...");
                    Plugin.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt, HookMode.Pre);
                }
            }
            catch (Exception ex)
            {
                if (ex.Message == "Invalid function pointer")
                    Utils.LogError($"Error in DamageHook: Conflict between cs2fixes and SharpTimer");
                else
                    Utils.LogError($"Error in DamageHook: {ex.Message}");
            }
        }

        public void Unhook()
        {
            Utils.LogDebug("Unhook RemoveDamage");

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (LinuxDamageHook != null)
                    {
                        InvokeLinuxDamageHook("Unhook");
                        LinuxDamageHook = null;
                    }
                }
                else
                {
                    Plugin.DeregisterEventHandler<EventPlayerHurt>(OnPlayerHurt, HookMode.Pre);
                }
            }
            catch (Exception ex)
            {
                if (ex.Message == "Invalid function pointer")
                    Utils.LogError($"Error in DamageUnHook: Conflict between cs2fixes and SharpTimer");
                else
                    Utils.LogError($"Error in DamageUnHook: {ex.Message}");
            }
        }

        private void InvokeLinuxDamageHook(string methodName)
        {
            if (LinuxDamageHook == null)
                throw new InvalidOperationException("Linux damage hook handle was not initialized");

            MethodInfo? hookMethod = LinuxDamageHook.GetType().GetMethod(methodName, [typeof(Func<DynamicHook, HookResult>), typeof(HookMode)]);
            if (hookMethod == null)
                throw new MissingMethodException($"Could not resolve {methodName} on Linux damage hook handle");

            hookMethod.Invoke(LinuxDamageHook, [(Func<DynamicHook, HookResult>)OnTakeDamage, HookMode.Pre]);
        }

        private HookResult OnTakeDamage(DynamicHook hook)
        {
            var ent = hook.GetParam<CEntityInstance>(0);
            var info = hook.GetParam<CTakeDamageInfo>(1);

            if (Plugin.disableDamage) hook.GetParam<CTakeDamageInfo>(1).Damage = 0;

            if (!ent.IsValid || !info.Attacker.IsValid)
                return HookResult.Continue;

            if (ent.DesignerName == "player" && info.Attacker.Value!.DesignerName == "player")
                return HookResult.Handled;
            else
                return HookResult.Continue;
        }

        private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
        {
            if (Plugin.disableDamage == true)
            {
                var player = @event.Userid;

                if (!player!.IsValid)
                    return HookResult.Continue;

                Vector playerSpeed = player!.PlayerPawn.Value!.AbsVelocity;

                player.PlayerPawn.Value.Health = int.MaxValue;
                player.PlayerPawn.Value.ArmorValue = int.MaxValue;

                if (!player.PawnHasHelmet)
                    player.GiveNamedItem("item_assaultsuit");

                Server.NextFrame(() =>
                {
                    if (Plugin.IsAllowedPlayer(player)) Plugin.AdjustPlayerVelocity(player, playerSpeed.Length(), true);
                });
            }

            return HookResult.Continue;
        }
    }
}
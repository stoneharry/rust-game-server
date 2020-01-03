using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SpectateManager", "stoneharry", "0.0.1")]
    class SpectateManager : RustPlugin
    {
        private HashSet<ulong> _SpectatorList = new HashSet<ulong>();

        #region ChatCommands
        [ChatCommand("spectate")]
        void SpectateCommand(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0 && _SpectatorList.Contains(player.userID))
            {
                EndSpectating(player, Vector3.zero);
                player.ChatMessage("Stopped spectating.");
                return;
            }
            var playerName = args[0];
            BasePlayer target = covalence.Players.Connected.FirstOrDefault(x => 
                x.Name.Equals(args[0], StringComparison.InvariantCultureIgnoreCase))
                ?.Object as BasePlayer;
            if (target != null && target.userID != player.userID)
            {
                StartSpectating(player, target);
                player.ChatMessage($"Spectating {playerName}.");
                return;
            }
            player.ChatMessage("Invalid specate command, expected player name or no arguments to stop spectating.");
        }
        #endregion

        #region Hooks
        void OnPluginLoaded(Plugin name)
        {
            if (!name.Name.Equals("SpectateManager"))
                return;
        }

        void OnPlayerInit(BasePlayer player)
        {
            player.SetPlayerFlag(BasePlayer.PlayerFlags.ThirdPersonViewmode, false);
        }
        #endregion

        #region SpectatingLogic
        private void StartSpectating(BasePlayer player, BasePlayer target)
        {
            player.spectateFilter = "@123nofilter123";
            player.SetPlayerFlag(BasePlayer.PlayerFlags.Spectating, true);
            player.SetPlayerFlag(BasePlayer.PlayerFlags.ThirdPersonViewmode, true);
            player.gameObject.SetLayerRecursive(10);
            player.CancelInvoke("InventoryUpdate");
            player.SendNetworkUpdateImmediate();

            timer.In(0.5f, () =>
            {
                player.transform.position = target.transform.position;
                player.SetParent(target, 0);
                player.Command("client.camoffset", new object[] { new Vector3(0, 3.5f, 0) });
            });

            _SpectatorList.Add(player.userID);
        }

        private void EndSpectating(BasePlayer player, Vector3 newLocation)
        {
            player.spectateFilter = string.Empty;
            player.SetPlayerFlag(BasePlayer.PlayerFlags.Spectating, false);
            player.SetPlayerFlag(BasePlayer.PlayerFlags.ThirdPersonViewmode, false);
            player.gameObject.SetLayerRecursive(17);
            player.InvokeRepeating("InventoryUpdate", 1f, 0.1f * UnityEngine.Random.Range(0.99f, 1.01f));
            player.SendNetworkUpdateImmediate();

            timer.In(0.5f, () =>
            {
                if (newLocation != Vector3.zero)
                {
                    player.transform.position = newLocation;
                }
                player.SetParent(null);
                player.Command("client.camoffset", new object[] { new Vector3(0, 1.2f, 0) });
            });

            _SpectatorList.Remove(player.userID);
        }
        #endregion
    }
}

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
    [Info("GameZoneCapture", "stoneharry", "0.0.1")]
    class GameZoneCapture : RustPlugin
    {
        private const string _GameName = "Zone Capture";
        private const string _ConfigFileName = "GameZoneCaptureConfig";

        private GameConfig _Config;
        
        #region GamePublicAPI_ForGameManager
        bool AddPlayer(BasePlayer player)
        {
            if (GetGameForPlayer(player.userID) != null)
            {
                Puts($"Failed to add player [{player.displayName}] to [{_GameName}], already in game");
                return false;
            }
            Puts($"Adding player [{player.displayName}] to game [{_GameName}]");
            return true;
        }

        bool RemovePlayer(BasePlayer player)
        {
            var toRemove = GetGameForPlayer(player.userID);
            if (toRemove != null)
            {
                Puts($"Removing player [{player.displayName}] from game [{_GameName}]");
                player.inventory?.Strip();
                var game = GetGameForPlayer(player.userID);
                toRemove.RemovePlayer(player, true);
                return true;
            }
            return false;
        }
        #endregion

        #region Hooks
        void OnPluginLoaded(Plugin name)
        {
            if (!name.Name.Equals("GameZoneCapture"))
                return;
            LoadConfigFile();
            WriteConfig();
        }

        //void OnServerInitialized() => GamesList.Games.ForEach((game) => game.SetPluginReferences(Kits));

        bool? OnPlayerDie(BasePlayer player, HitInfo info)
        {
            return null;
        }

        object OnPlayerRespawn(BasePlayer player)
        {
            var game = GetGameForPlayer(player.userID);
            if (game != null)
            {
                return new BasePlayer.SpawnPoint()
                {
                    pos = _Config.GetSpawnPosition(),
                    rot = new Quaternion(0, 0, 0, 1)
                };
            }
            return null;
        }

        bool? OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            return null;
        }
        #endregion

        #region GameLogic
        private Game GetGameForPlayer(ulong userId)
        {
            /*foreach (Game game in GamesList.Games)
            {
                if (game.HasPlayer(userId))
                    return game;
            }*/
            return null;
        }
        #endregion

        #region GamePlayerClass
        private class GamePlayer
        {
            public readonly ulong UserID;
            public readonly string DisplayName;
            public readonly BasePlayer Entity;
            private Vector3 homePosition;

            public GamePlayer(int team, BasePlayer player, int startingLives)
            {
                DisplayName = player.displayName;
                UserID = player.userID;
                Entity = player;
                homePosition = player.transform.position;
            }

            public void SendHome()
            {
                if (Entity == null)
                {
                    return;
                }
                Entity.inventory.Strip();
                Entity.Teleport(homePosition);
            }

            public void Destroy()
            {

            }
        }
        #endregion

        #region GameClass
        private class Game
        {
            public List<Vector3> TeamPositions { get; set; } = new List<Vector3>();
            private readonly Dictionary<ulong, GamePlayer> players = new Dictionary<ulong, GamePlayer>();
            public List<string> StartingKitNames { get; set; }
            public Plugin KitsPluginRef { get; set; }
            public bool Active => players.Count > 0;

            public Game(Plugin kitsPluginRef)
            {
                KitsPluginRef = kitsPluginRef;
            }

            public void SetPluginReferences(Plugin kitsPluginRef)
            {
                KitsPluginRef = kitsPluginRef;
            }

            public void ClearPlayers()
            {
                var keys = new List<ulong>();
                foreach (ulong uid in players.Keys)
                    keys.Add(uid);
                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    if (player != null && keys.Contains(player.userID))
                        RemovePlayer(player, true);
                }
            }
            
            
            public void AddPlayer(BasePlayer player, int team)
            {
               // players.Add(player.userID, new GamePlayer(newTeam, player, StartingLives));
            }

            public void ResetPlayerToSpawn(BasePlayer player)
            {

            }

            public void ResetPlayerInventory(BasePlayer player)
            {

            }

            public void RemovePlayer(BasePlayer player, bool teleport)
            {
                if (!players.ContainsKey(player.userID))
                    return;
                GamePlayer gamePlayer = players[player.userID];
                players.Remove(player.userID);
                gamePlayer.SendHome();
            }

            public bool HasPlayer(ulong userId) => players.ContainsKey(userId);
            public GamePlayer GetPlayer(ulong userId)
            {
                GamePlayer player;
                if (players.TryGetValue(userId, out player))
                {
                    return player;
                }
                return null;
            }
        }
        #endregion

        #region ConfigFunctionsAndClass
        private void LoadConfigFile()
        {
            var fileStore = Interface.Oxide.DataFileSystem.GetFile(_ConfigFileName);
            _Config = fileStore?.ReadObject<GameConfig>();
            if (_Config != null && _Config.SpawnPosition != null && _Config.SpawnPosition.Count == 3)
            {
                Puts("Loaded config");
                return;
            }
            else
            {
                _Config = GenerateDefaultConfigFile();
                Puts("Failed to load config, generated new one");
            }
        }

        private void WriteConfig()
        {
            if (_Config == null)
            {
                return;
            }
            Interface.Oxide.DataFileSystem.WriteObject(_ConfigFileName, _Config);
        }

        private GameConfig GenerateDefaultConfigFile()
        {
            return new GameConfig()
            {
                GameName = _GameName,
                StartingKitName = "ZoneCaptureStart",
                SpawnPosition = new List<float>(new float[] { 399.0045f, 4.268373f, -456.5683f })
            };
        }

        public class GameConfig
        {
            public string GameName = "";
            public string StartingKitName = "";
            public List<float> SpawnPosition = new List<float>();
            
            public GameConfig()
            {
            }

            public Vector3 GetSpawnPosition() => new Vector3(SpawnPosition[0], SpawnPosition[1], SpawnPosition[2]);
        }
        #endregion

    }
}

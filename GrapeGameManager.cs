using System;
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Core.Plugins;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("GrapeGameManager", "stoneharry", "0.0.1")]
    public class GrapeGameManager : RustPlugin
    {
        private const string ZombieDefenceGameName = "Zombie Defence";

        private const string _FileStoreName = "GameManagerConfig";
        private GameManagerInstance _Instance = null;
        private GameConfig _Config = null;

        #region Commands
        [ChatCommand("game")]
        void JoinGameCommand(BasePlayer player, string command, string[] args)
        {
            if (_Instance == null)
            {
                return;
            }
            if (args.Length < 1)
            {
                player.ChatMessage("Input game ID to join, list of games:");
                foreach (var pair in _Config.GameInstanceMap)
                {
                    player.ChatMessage($"Game ID: {pair.Key}, {pair.Value.GameName}");
                }
                return;
            }
            ulong gameId;
            if (!ulong.TryParse(args[0], out gameId))
            {
                player.ChatMessage($"Invalid Game ID {gameId}");
                return;
            }
            if (_Instance.ContainsPlayer(player.userID))
            {
                player.ChatMessage("You are already in a game.");
                return;
            }
            var game = _Instance.GetGameInstance(gameId);
            if (game == null)
            {
                player.ChatMessage($"Unable to find game ID {gameId}");
                return;
            }
            if (game.AddPlayer(new GamePlayer(player)))
            {
                player.ChatMessage($"Successfully joined game ID {gameId}");
            }
            else
            {
                player.ChatMessage($"Failed to join game ID {gameId}");
            }
        }

        [ChatCommand("leave")]
        void LeaveGameCommand(BasePlayer player, string command, string[] args)
        {
            if (_Instance == null)
            {
                return;
            }
            if (_Instance.RemovePlayer(player.userID))
            {
                player.ChatMessage("Left game.");
            }
            else
            {
                player.ChatMessage("You are not in a game to leave.");
            }
        }
        #endregion

        #region PublicAPI
        public void PlayerRemovedFromGameHook(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }
            _Instance.RemovePlayer(player.userID);
        }
        #endregion

        #region Hooks
        bool? OnPlayerLand(BasePlayer player)
        {
            if (_Instance != null && _Instance.ContainsPlayer(player.userID))
            {
                return null;
            }
            return true;
        }

        void OnPluginLoaded(Plugin name)
        {
            if (!name.Name.Equals("GrapeGameManager"))
                return;
            _Instance?.Destroy();
            // Process after a delay to ensure plugin references have been wired up
            timer.Once(1.5f, () =>
            {
                LoadConfigFile();
                WriteConfig();
                var _GamePluginMap = new Dictionary<string, Plugin>();
                foreach (var gameConfig in _Config.GameInstanceMap.Values)
                {
                    _GamePluginMap.Add(gameConfig.GameName, plugins.Find(gameConfig.GamePluginName));
                }
                _Instance = new GameManagerInstance(_Config, _GamePluginMap);
            });
        }

        // On plugin unloaded hook
        void Unload()
        {
            _Instance?.Destroy();
        }

        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (_Instance != null && _Instance.ContainsPlayer(player.userID))
            {
                _Instance.RemovePlayer(player.userID);
            }
        }

        private void OnEntitySpawned(BaseEntity spawned)
        {
            if (spawned.name != null && spawned.name.Equals("assets/prefabs/misc/item drop/item_drop_backpack.prefab"))
            {
                timer.In(60f, () => 
                {
                    if (spawned != null && !spawned.IsDestroyed)
                    {
                        spawned.Kill();
                    }
                });
            }
        }

        bool? OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (_Instance == null)
            {
                return null;
            }
            // If entity being attacked is a normal player and
            // the initiator is not a npc player
            if (entity is BasePlayer && !(entity is NPCPlayer) && !(entity is HTNPlayer) &&
                info.Initiator != null && !(info.Initiator is NPCPlayer || info.Initiator is HTNPlayer))
            {
                if (!_Instance.ContainsPlayer(entity.ToPlayer().userID))
                {
                    info.DidHit = false;
                    return true;
                }
            }
            // Disable damage to these types
            if (entity is BuildingBlock ||
                entity is AnimatedBuildingBlock ||
                entity is Signage || 
                //entity is StorageContainer*/
                entity is Barricade /*||
                entity is DecayEntity*/)
            {
                info.damageTypes = new DamageTypeList();
            }
            return null;
        }
        #endregion

        #region ConfigFunctionsAndClass
        private void LoadConfigFile()
        {
            var fileStore = Interface.Oxide.DataFileSystem.GetFile(_FileStoreName);
            _Config = fileStore?.ReadObject<GameConfig>();
            if (_Config != null && _Config.GameInstanceMap != null && _Config.GameInstanceMap.Count > 0)
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
            Interface.Oxide.DataFileSystem.WriteObject(_FileStoreName, _Config);
        }

        private GameConfig GenerateDefaultConfigFile()
        {
            return new GameConfig()
            {
                GameInstanceMap = new Dictionary<ulong, GameInstanceConfig>
                {
                    { 1, new GameInstanceConfig
                        {
                            GameName = ZombieDefenceGameName,
                            GamePluginName = "GameZombieDefence"
                        }
                    }
                }
            };
        }

        public class GameConfig
        {
            public Dictionary<ulong, GameInstanceConfig> GameInstanceMap = new Dictionary<ulong, GameInstanceConfig>();
            public GameConfig()
            {
            }
        }

        public class GameInstanceConfig
        {
            public string GameName = "";
            public string GamePluginName = "";
        }
        #endregion

        #region GameClasses
        class GameManagerInstance
        {
            private List<GameInstance> _GameInstances = new List<GameInstance>();

            public GameManagerInstance(GameConfig config, Dictionary<string, Plugin> gamePluginMap)
            {
                foreach (var instanceConfig in config.GameInstanceMap)
                {
                    var instanceId = instanceConfig.Key;
                    if (GetGameInstance(instanceId) != null)
                    {
                        Console.WriteLine($"ERROR: Game Instance ID {instanceId} already created, skipping duplicate creation");
                        continue;
                    }
                    var gameName = instanceConfig.Value.GameName;
                    Plugin gamePlugin = null;
                    if (!gamePluginMap.TryGetValue(gameName, out gamePlugin))
                    {
                        Console.WriteLine($"Could not find plugin reference [{gameName}] for game instance [{instanceId}]");
                        continue;
                    }
                    _GameInstances.Add(new GameInstance(instanceId, gamePlugin)
                    {
                        GameName = gameName
                    });
                    Console.WriteLine($"Created game instance [{instanceId} = \"{gameName}\"]");
                }
            }

            public bool ContainsPlayer(ulong userID)
            {
                foreach (var instance in _GameInstances)
                {
                    if (instance.ContainsPlayer(userID))
                    {
                        return true;
                    }
                }
                return false;
            }

            public bool RemovePlayer(ulong userId)
            {
                bool removedPlayer = false;
                foreach (var instance in _GameInstances)
                {
                    var player = instance.GetGamePlayer(userId);
                    if (player != null)
                    {
                        removedPlayer = instance.RemovePlayer(player) || removedPlayer;
                    }
                }
                return removedPlayer;
            }

            public GameInstance GetGameInstance(ulong id)
            {
                foreach (var instance in _GameInstances)
                {
                    if (instance.ID == id)
                    {
                        return instance;
                    }
                }
                return null;
            }

            public void Destroy()
            {
                foreach (var instance in _GameInstances)
                {
                    instance.Destroy();
                }
            }
        }

        class GameInstance
        {
            private Dictionary<ulong, GamePlayer> _Players = new Dictionary<ulong, GamePlayer>();

            public bool ContainsPlayer(ulong userID) => _Players.ContainsKey(userID);
            public readonly Plugin GamePlugin;
            public readonly ulong ID;
            public string GameName;

            public GameInstance(ulong id, Plugin plugin)
            {
                ID = id;
                GamePlugin = plugin;
            }

            public bool AddPlayer(GamePlayer player)
            {
                if (GamePlugin == null)
                {
                    Console.WriteLine("ERROR: Plugin reference null on AddPlayer");
                    return false;
                }
                if (GamePlugin.Call<bool>("AddPlayer", player.Entity))
                {
                    _Players.Add(player.UserID, player);
                    return true;
                }
                return false;
            }

            public bool RemovePlayer(GamePlayer player)
            {
                if (GamePlugin == null)
                {
                    Console.WriteLine("ERROR: Plugin reference null on RemovePlayer");
                    return false;
                }
                GamePlugin.Call<bool>("RemovePlayer", player.Entity);
                return _Players.Remove(player.UserID);
            }

            public GamePlayer GetGamePlayer(ulong userId)
            {
                GamePlayer gamePlayer = null;
                _Players.TryGetValue(userId, out gamePlayer);
                return gamePlayer;
            }

            public void Destroy()
            {
                // Collect then remove as _Players gets modified when we call RemovePlayer
                var toRemove = new List<GamePlayer>(_Players.Values.Count);
                foreach (var player in _Players.Values)
                {
                    toRemove.Add(player);
                }
                foreach (var player in toRemove)
                {
                    RemovePlayer(player);
                    player.Destroy();
                }
                //GamePlugin?.Call("Destroy");
            }
        }

        class GamePlayer
        {
            public readonly ulong UserID;
            public readonly BasePlayer Entity;

            public GamePlayer(BasePlayer player)
            {
                UserID = player.userID;
                Entity = player;
            }

            public void Destroy()
            {

            }
        }
        #endregion
    }
}
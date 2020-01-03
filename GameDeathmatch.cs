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
    [Info("GameDeathmatch", "stoneharry", "0.0.1")]
    class GameDeathmatch : RustPlugin
    {
        private const string _GameName = "Deathmatch";
        private const string _ConfigFileName = "DeathmatchConfig";
        
        private GameConfig _Config = null;
        private GamesContainer GamesList = new GamesContainer();

        private const float ScoreboardUpdateRate = 3f;
        private Timer _ScoreboardTicker;

        [PluginReference]
        Plugin Kits;
        [PluginReference]
        Plugin ScoreBoard;

        private class GamesContainer
        {
            public List<Game> Games { get; set; } = new List<Game>();
            public Game GetGameByName(string name)
            {
                foreach (Game game in Games)
                {
                    if (string.CompareOrdinal(game.ArenaName, name) == 0)
                        return game;
                }
                return null;
            }

            public void Add(Game newGame)
            {
                Games.Add(newGame);
            }

            public void Destroy()
            {
                foreach (var game in Games)
                {
                    game.ClearPlayers();
                }
                Games.Clear();
            }
        }

        #region GamePublicAPI_ForGameManager
        bool AddPlayer(BasePlayer player)
        {
            if (GetGameForPlayer(player.userID) != null)
            {
                Puts($"Failed to add player [{player.displayName}] to [{_GameName}], already in game");
                return false;
            }
            Puts($"Adding player [{player.displayName}] to game [{_GameName}]");
            GamesList.Games[0].AddPlayer(player, -1);
            NextTick(() => GamesList.Games[0].ResetPlayerInventory(player));
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
                CuiHelper.DestroyUi(player, ScoreboardPrefix + game.ArenaName);
                toRemove.RemovePlayer(player, true);
                return true;
            }
            return false;
        }

        void Unload()
        {
            GamesList?.Destroy();
            _ScoreboardTicker?.Destroy();
        }
        #endregion

        #region Hooks
        void OnPluginLoaded(Plugin name)
        {
            if (!name.Name.Equals("GameDeathmatch"))
                return;
            LoadConfigFile();
            WriteConfig();
            _ScoreboardTicker?.Destroy();
            _ScoreboardTicker = timer.Every(ScoreboardUpdateRate, () =>
            {
                foreach (Game game in GamesList.Games)
                {
                    if (game.Active)
                        UpdateScoreboard(game);
                }
            });
            GamesList.Destroy();
            GamesList.Add(new Game(Kits, _Config)
            {
                StartingKitNames = new List<string>(new string[] { _Config.StartingKitName }),
                StartingLives = -1,
                ArenaName = "Deathmatch",
                FreeForAll = true
            });
        }

        void OnServerInitialized() => GamesList.Games.ForEach((game) => game.SetPluginReferences(Kits));

        bool? OnPlayerDie(BasePlayer player, HitInfo info)
        {
            var game = GetGameForPlayer(player.userID);
            if (game == null)
                return null;
            game.Died(player.userID);
            BaseEntity initiator = info.Initiator;
            if (initiator is BasePlayer && GetGameForPlayer(initiator.ToPlayer().userID) != null)
                game.GotKill(initiator.ToPlayer().userID);
            if (game.IsAfterDeathHandleOutOfLives(player))
            {
                // Do something
            }
            return null;
        }

        object OnPlayerRespawn(BasePlayer player)
        {
            var game = GetGameForPlayer(player.userID);
            if (game != null)
            {
                NextTick(() => 
                {
                    game.ResetPlayerToSpawn(player);
                    game.ResetPlayerInventory(player);
                });
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
            if (entity is BasePlayer)
            {
                var initiator = info.Initiator;
                if (!(initiator is BasePlayer))
                    return null;
                BasePlayer target = entity.ToPlayer();
                BasePlayer attacker = initiator.ToPlayer();
                object gameResultA = IsPlayerInCache(target.userID);
                object gameResultB = IsPlayerInCache(attacker.userID);
                if (gameResultA is bool && (bool)gameResultA ||
                    gameResultB is bool && (bool)gameResultB)
                    return null;
                Game targetGame = GetGameForPlayer(target.userID);
                Game attackerGame = GetGameForPlayer(attacker.userID);
                if (targetGame == null && attackerGame == null)
                    return null;
                if (targetGame == null || attackerGame == null)
                    return null;
                if (!attackerGame.Equals(targetGame))
                    return null;
                // Need to actually retrieve detailed information on next server tick, because HitInfo will not have been scaled according to hitboxes, protection, etc until then:
                NextTick(() =>
                {
                    SendReply(attacker, attackerGame.HandlePlayerFullAttack(info.damageTypes.Total(), attacker, target.userID));
                    UpdateScoreboard(attackerGame);
                });
                return attackerGame.HandlePlayerAttack(attacker, target.userID);
            }
            return null;
        }
        #endregion

        #region GameLogic
        private bool? IsPlayerInCache(ulong userId) => GamesList?.Games.Any(check => check.HasPlayer(userId));

        private Game GetGameForPlayer(ulong userId)
        {
            foreach (Game game in GamesList.Games)
            {
                if (game.HasPlayer(userId))
                    return game;
            }
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
            public int Team { get; set; }
            public int Lives { get; set; } = 3;
            public float Score { get; set; } = 0;
            public uint Kills { get; set; } = 0;
            public uint Deaths { get; set; } = 0;
            public int FriendlyScore => Convert.ToInt32(Score);

            public GamePlayer(int team, BasePlayer player, int startingLives)
            {
                DisplayName = player.displayName;
                Team = team;
                UserID = player.userID;
                Entity = player;
                homePosition = player.transform.position;
                Lives = startingLives;
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
                SendHome();
            }

            public int DeductLives()
            {
                if (Lives != -1)
                    Lives -= 1;
                return Lives;
            }

            public void GotKill()
            {
                ++Kills;
            }

            public void Died()
            {
                ++Deaths;
            }

            public string FriendlyFire(BasePlayer attacker, float damage)
            {
                Score -= damage;
                return $"{WrapColor("red", "-" + Convert.ToUInt32(damage))} points";
            }

            public string HitEnemy(BasePlayer player, float damage)
            {
                Score += damage;
                return $"{WrapColor("green", "+" + Convert.ToUInt32(damage))} points";
            }
        }
        #endregion

        #region GameClass
        private class Game
        {
            private readonly Dictionary<ulong, GamePlayer> players = new Dictionary<ulong, GamePlayer>();
            public int StartingLives { get; set; } = 3;
            public List<string> StartingKitNames { get; set; }
            public Plugin KitsPluginRef { get; set; }
            public string ArenaName { get; set; } = "Default";
            public bool RandomSpawns { get; set; } = true;
            public bool FriendlyFire { get; set; } = true;
            public bool FreeForAll { get; set; } = true;
            public bool Active => players.Count > 0;
            private GameConfig _Config;

            public Game(Plugin kitsPluginRef, GameConfig config)
            {
                _Config = config;
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

            public void GotKill(ulong userId)
            {
                if (!players.ContainsKey(userId))
                    return;
                GamePlayer player = players[userId];
                player.GotKill();
            }

            public void Died(ulong userId)
            {
                if (!players.ContainsKey(userId))
                    return;
                GamePlayer player = players[userId];
                player.Died();
            }

            public bool IsAfterDeathHandleOutOfLives(BasePlayer player)
            {
                var gamePlayer = players[player.userID];
                var lives = gamePlayer.DeductLives();
                switch (lives)
                {
                    case 0:
                        int score = gamePlayer.FriendlyScore;
                        player.SendConsoleCommand("chat.add", 0,
                            $"You are out of lives! Final score {WrapColor(score > 0 ? "green" : "red", score.ToString())}.", 1f);
                        return true;
                    case -1:
                        player.SendConsoleCommand("chat.add", 0, "You have infinite lives in this game mode! To leave use /leave", 1f);
                        break;
                    default:
                        player.SendConsoleCommand("chat.add", 0, "You have " + lives + " lives remaining!", 1f);
                        break;
                }
                return false;
            }

            public void AddPlayer(BasePlayer player, int team)
            {
                // magic numbers should be refactored
                /*if (team != -420 && team != -1 && (team < 1 || team > TeamPositions.Count))
                    return;
                var newTeam = team == -1 ? UnityEngine.Random.Range(0, TeamPositions.Count) : team;*/
                var newTeam = -1;
                players.Add(player.userID, new GamePlayer(newTeam, player, StartingLives));
                if (team != -420)
                    ResetPlayerToSpawn(player);
            }

            public void ResetPlayerToSpawn(BasePlayer player)
            {
                if (!players.ContainsKey(player.userID))
                    return;
                player.inventory.Strip();
                /*player.Teleport(RandomSpawns
                    ? TeamPositions[UnityEngine.Random.Range(0, TeamPositions.Count)]
                    : TeamPositions[players[player.userID].Team - 1]);*/
                player.Teleport(_Config.GetSpawnPosition());

                player.health = 100f;
                player.metabolism.calories.value = 500f;
                player.metabolism.hydration.value = 500f;
                player.metabolism.bleeding.value = 0f;
                player.metabolism.bleeding.Reset();
            }

            public void ResetPlayerInventory(BasePlayer player)
            {
                player.inventory.Strip();
                if (KitsPluginRef != null)
                {
                    var team = players[player.userID].Team;
                    if (team == -1)
                    {
                        team = UnityEngine.Random.Range(0, StartingKitNames.Count);
                    }
                    if (team >= StartingKitNames.Count)
                    {
                        player.SendConsoleCommand("chat.add", 0,
                            $"Failed to add kit, no kit exists for teams above {StartingKitNames.Count}, your team is {team}", 1f);
                    }
                    else
                    {
                        var kitName = StartingKitNames[team];
                        object value = KitsPluginRef.Call("GiveKit", player, kitName);
                        if (value is string)
                            player.SendConsoleCommand("chat.add", 0, "Failed to add starting kit [" + kitName + "]", 1f);
                    }
                }
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

            public string HandlePlayerFullAttack(float damage, BasePlayer attacker, ulong targetId)
            {
                ulong attackerId = attacker.userID;
                if (!HasPlayer(attackerId) || !HasPlayer(targetId))
                {
                    if (HasPlayer(attackerId) || HasPlayer(targetId))
                        return null;
                }
                GamePlayer player1 = players[attackerId];
                GamePlayer player2 = players[targetId];
                if (FriendlyFire && !FreeForAll && player1.Team == player2.Team)
                    return player1.FriendlyFire(attacker, damage);
                return player1.HitEnemy(attacker, damage);
            }

            public bool? HandlePlayerAttack(BasePlayer attacker, ulong targetId)
            {
                ulong attackerId = attacker.userID;
                if (!HasPlayer(attackerId) || !HasPlayer(targetId))
                {
                    if (HasPlayer(attackerId) || HasPlayer(targetId))
                        return true; // Shouldn't happen, if it does disable damage
                }
                if (!FriendlyFire && !FreeForAll && players[attackerId].Team == players[targetId].Team)
                    return true; // Disable damage on friendly fire
                return null;
            }

            public string GenerateScoreboardString()
            {
                var sortedPlayers = new List<GamePlayer>();
                foreach (var player in players.Values)
                {
                    sortedPlayers.Add(player);
                }
                sortedPlayers.Sort(new ScoreboardComparer());

                int playersToDisplay = players.Count < 7 ? players.Count : 7;
                int shownPlayers = 0;
                StringBuilder str = new StringBuilder();
                str.Append("Top " + playersToDisplay + " players in " + ArenaName + "\n");
                str.Append("Name        | Score | Kills | Deaths \n");
                for (var i = sortedPlayers.Count - 1; i >= 0; --i)
                {
                    if (shownPlayers++ == playersToDisplay)
                    {
                        break;
                    }
                    GamePlayer player = sortedPlayers[i];
                    str.Append(string.Format(
                        "{3,-11} | {0,-5} | {1,-5} | {2,-6} \n",
                        player.FriendlyScore.ToString(),
                        player.Kills.ToString(),
                        player.Deaths.ToString(),
                        player.DisplayName));
                }
                return str.ToString();
            }

            public HashSet<ulong> GetUserIds()
            {
                var userIds = new HashSet<ulong>();
                foreach (var key in players.Keys)
                    userIds.Add(key);
                return userIds;
            }
        }

        class ScoreboardComparer : IComparer<GamePlayer>
        {
            public int Compare(GamePlayer x, GamePlayer y)
            {
                if (x.FriendlyScore > y.FriendlyScore)
                {
                    return 1;
                }
                else if (x.FriendlyScore < y.FriendlyScore)
                {
                    return -1;
                }
                return 0;
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
                StartingKitName = "DeathmatchStart",
                SpawnMode = "RANDOM_SQUARE",
                SpawnPosition = new List<List<float>>(new List<float>[] {
                    new List<float>(new float[] { 850.3586f, 879.6567f }),
                    new List<float>(new float[] { -856.3438f, -829.5442f })
                })
            };
        }

        public class GameConfig
        {
            public string GameName = "";
            public string StartingKitName = "";
            public string SpawnMode = "";
            public List<List<float>> SpawnPosition = new List<List<float>>();
            
            public GameConfig()
            {
            }

            public Vector3 GetSpawnPosition()
            {
                if (SpawnPosition.Count == 0)
                {
                    Console.WriteLine($"No spawn positions detected for [{GameName}]");
                    return Vector3.zero;
                }
                SpawnModeEnum mode;
                if (!Enum.TryParse(SpawnMode, out mode))
                {
                    mode = SpawnModeEnum.RANDOM_POINT;
                }
                if (mode == SpawnModeEnum.RANDOM_SQUARE)
                {
                    if (SpawnPosition.Count != 2)
                    {
                        Console.WriteLine($"Unable to load [{GameName}] spawn positions, expected 2 lists of 2 values (x,z coordinates)");
                        return new Vector3(SpawnPosition[0][0], SpawnPosition[0][1], SpawnPosition[0][2]);
                    }
                    var values1 = SpawnPosition[0];
                    var values2 = SpawnPosition[1];
                    var position = FindRandomPosition(values1[0], values1[1], values2[0], values2[1]);
                    return position;
                }
                var point = UnityEngine.Random.Range(0, SpawnPosition.Count);
                return new Vector3(SpawnPosition[point][0], SpawnPosition[point][1], SpawnPosition[point][2]);
            }

            enum SpawnModeEnum
            {
                // top left and bottom right coordinates expected for random grid spawn
                RANDOM_SQUARE,
                // use one of the random points provided
                RANDOM_POINT
            }
        }
        #endregion

        #region GroundPositionCalculators
        private static Vector3 FindRandomPosition(float x1, float x2, float z1, float z2)
        {
            for (int i = 0; i < 20; ++i)
            {
                var x = UnityEngine.Random.Range((int)x1, (int)x2);
                var z = UnityEngine.Random.Range((int)z1, (int)z2);
                var pos = CalculateGroundPos(new Vector3(x, 200, z), true);
                if (pos != Vector3.zero)
                {
                    pos.y += 5f;
                    return pos;
                }
            }
            return new Vector3();
        }

        private static Vector3 CalculateGroundPos(Vector3 sourcePos, bool Biome)
        {
            RaycastHit hitInfo;
            var cast = Physics.Raycast(sourcePos, Vector3.down, out hitInfo, 1000f, LayerMask.GetMask("Terrain", "Water", "World"), QueryTriggerInteraction.Ignore);
            if (!hitInfo.collider.name.Contains("rock"))
                cast = Physics.Raycast(sourcePos, Vector3.down, out hitInfo, 1000f, LayerMask.GetMask("Terrain", "Water"), QueryTriggerInteraction.Ignore);
            if (hitInfo.collider.tag == "Main Terrain" || hitInfo.collider.name.Contains("rock"))
            {
                sourcePos.y = hitInfo.point.y;
                return sourcePos;
            }
            return Vector3.zero;
        }
        #endregion

        #region Scoreboard
        private static readonly string ScoreboardPrefix = "Scoreboard";
        private void UpdateScoreboard(Game game)
        {
            var scoreboardName = ScoreboardPrefix + game.ArenaName;
            var scoreboardString = game.GenerateScoreboardString();
            var element = ScoreBoard?.Call(
                "GetScoreboardElement",
                scoreboardName,
                scoreboardString);
            if (element == null)
                return;
            var userIds = game.GetUserIds();
            ScoreBoard.Call(
                "RenderScoreboardToPlayers",
                element,
                scoreboardName,
                userIds);
        }
        #endregion

        public static string WrapColor(string colour, string input)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(colour))
                return input;
            return $"<color={colour}>{input}</color>";
        }
    }
}

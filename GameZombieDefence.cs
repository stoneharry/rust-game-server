using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Rust.Ai.HTN.Scientist;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("GameZombieDefence", "stoneharry", "0.0.1")]
    class GameZombieDefence : RustPlugin
    {
        private const string _GameName = "Zombie Defence";
        private const string _ConfigFileName = "ZombieDefenceConfig";
        private const string _ScarecrowPrefab = "assets/prefabs/npc/scarecrow/scarecrow.prefab";
        private const string _MurdererPrefab = "assets/prefabs/npc/murderer/murderer.prefab";
        private const string _ScientistPrefab = "assets/prefabs/npc/scientist/scientist.prefab";
        private const string _TankPrefab = "assets/prefabs/npc/m2bradley/bradleyapc.prefab";

        [PluginReference]
        Plugin ScoreBoard;
        [PluginReference]
        Plugin GameDefenceCui;
        [PluginReference]
        Plugin Kits;
        [PluginReference]
        Plugin GrapeGameManager;

        private GameConfig _Config = null;
        private Timer _GameUpdater = null;
        private System.Random _Random = new System.Random();
        private const float _ScoreboardUpdateRate = 3f;
        private const float _TimeAndObjectiveUpdateRate = 0.9f;
        private const float _ItemDespawnTime = 60 * 5;
        private Timer _ScoreboardTicker;
        private Timer _TimeAndObjectiveTicker;
        private RunningGameData _GameData;

        private enum GAME_STATE
        {
            WAITING_FOR_PLAYERS,
            COUNTDOWN_WAVE,
            RUNNING_WAVE,
            TANK_BOSS_1,
            PLAYERS_WIN,
            ALL_PLAYERS_DEAD
        }

        #region DebugCommands
        [ChatCommand("add")]
        void AddCommand(BasePlayer player, string command, string[] args)
        {
            if (player.IsAdmin)
            {
                player.ChatMessage("Adding item");
                player.GiveItem(ItemManager.CreateByName(args[0], 1, ulong.Parse(args[1])));
            }
        }

        [ChatCommand("wave")]
        void WaveCommand(BasePlayer player, string command, string[] args)
        {
            if (player.IsAdmin)
            {
                bool worked = _GameData.DebugState(int.Parse(args[0]));
                player.ChatMessage($"Setting wave to { args[0] } { (worked ? "worked" : "failed") }");
            }
        }

        [ChatCommand("delete")]
        void deletesphere(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
                return;
            if (args.Length < 1)
            {
                RaycastHit hit;
                BaseEntity sphere = null;
                if (Physics.Raycast(player.eyes.HeadRay(), out hit, 20f))
                    sphere = hit.transform.GetComponentInParent<BaseEntity>();
                if (sphere != null)
                {
                    player.SendConsoleCommand("chat.add", 0, "worked " + sphere, 1f);
                    sphere.Kill();
                }
                else
                {
                    player.SendConsoleCommand("chat.add", 0, "targeting: " + hit.GetEntity() + "\n" + hit.GetCollider() + "\n" + hit.GetRigidbody(), 1f);
                }
                return;
            }
        }
        #endregion

        #region GamePublicAPI_ForGameManager
        bool AddPlayer(BasePlayer player)
        {
            if (FindPlayerByUserId(player.userID) != null)
            {
                Puts($"Failed to add player [{player.displayName}] to [{_GameName}], already in game");
                return false;
            }
            Puts($"Adding player [{player.displayName}] to game [{_GameName}]");
            _GameData.Players.Add(new GamePlayer(player));
            InitialiseGamePlayer(player);
            Puts($"New player count: {_GameData.Players.Count}");
            return true;
        }

        bool RemovePlayer(BasePlayer player)
        {
            var toRemove = FindPlayerByUserId(player.userID);
            if (toRemove != null)
            {
                Puts($"Removing player [{player.displayName}] from game [{_GameName}]");
                toRemove.SendHome();
                bool removed = _GameData.Players.Remove(toRemove);
                CuiHelper.DestroyUi(player, ScoreboardPrefix + _GameName);
                GameDefenceCui?.Call("DestroyGUIExternal", player);
                return removed;
            }
            return false;
        }

        void Unload()
        {
            // Loop on players without having a lock on the Players list so it can be modified
            while (_GameData.Players.Count > 0)
            {
                GrapeGameManager?.Call("PlayerRemovedFromGameHook", _GameData.Players[0].Entity);
                RemovePlayer(_GameData.Players[0].Entity);
            }
            _GameData.NPCPlayers.ForEach((entity) => entity.Kill());
            _GameData.NPCPlayers.Clear();
        }
        #endregion

        #region Hooks
        void OnPluginLoaded(Plugin name)
        {
            if (!name.Name.Equals("GameZombieDefence"))
                return;
            LoadConfigFile();
            WriteConfig();
            _GameData = new RunningGameData(new WaveConfiguration(), _Config);
            _GameUpdater?.Destroy();
            _GameUpdater = timer.Every(1f, () => GameUpdate());
            _ScoreboardTicker?.Destroy();
            _ScoreboardTicker = timer.Every(_ScoreboardUpdateRate, () => UpdateScoreboard());
            _TimeAndObjectiveTicker?.Destroy();
            _TimeAndObjectiveTicker = timer.Every(_TimeAndObjectiveUpdateRate, () => UpdateTimeAndObjective());
        }

        bool? OnPlayerDie(BasePlayer player, HitInfo info)
        {
            var gamePlayer = FindPlayerByUserId(player.userID);
            if (gamePlayer != null)
            {
                // Only count a death if not already wounded
                if (!player.IsWounded())
                {
                    gamePlayer.AddDeath();
                }
                player.StartWounded();
                player.ProlongWounding(60 * 10);
                return true;
            }
            return null;
        }

        object OnPlayerRespawn(BasePlayer player)
        {
            var gamePlayer = FindPlayerByUserId(player.userID);
            if (gamePlayer != null)
            {
                InitialiseGamePlayer(player);
                return new BasePlayer.SpawnPoint()
                {
                    pos = _Config.GetSpawnPosition(),
                    rot = new Quaternion(0, 0, 0, 1)
                };
            }
            return null;
        }

        private void OnEntitySpawned(BaseEntity spawned)
        {
            HandleNpcLootChange(spawned);
        }

        void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity != null && (entity is NPCPlayer || entity is HTNPlayer) && info.Initiator != null && info.Initiator is BasePlayer)
            {
                var player = info.Initiator.ToPlayer();
                var gamePlayer = FindPlayerByUserId(player.userID);
                if (gamePlayer != null)
                {
                    gamePlayer.AddKill();
                }
                var data = entity.GetComponent<ZombieData>();
                if (data != null && data.MinScrap > 0 && data.MaxScrap > 0)
                {
                    var item = ItemManager.CreateByName("scrap", UnityEngine.Random.Range(data.MinScrap, data.MaxScrap));
                    if (item != null)
                    {
                        var velocity = entity.GetComponent<Rigidbody>()?.velocity;
                        item.Drop(entity.transform.position + new Vector3(0, 0.25f, 0), velocity ?? Vector3.zero);
                        timer.Once(_ItemDespawnTime, () => item?.RemoveFromWorld());
                    }
                }
            }
        }

        object OnNpcPlayerTarget(NPCPlayerApex npcPlayer, BaseEntity entity)
        {
            if (npcPlayer == null || entity == null)
                return null;
            return CanZombieTarget(entity);
        }

        object CanBradleyApcTarget(BradleyAPC apc, BaseEntity entity)
        {
            if (entity == null || apc == null)
                return null;
            return CanZombieTarget(entity);
        }

        object CanZombieTarget(BaseEntity entity)
        {
            var isNpc = (entity is HTNPlayer) || (entity is NPCPlayer) || (entity is Scientist);
            if (isNpc)
                return true;
            if (entity is BasePlayer)
            {
                if (entity.ToPlayer().IsWounded())
                    return true;
            }
            return null;
        }

        bool? OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            // Reflect friendly damage
            if (entity is BasePlayer && info.Initiator != null && info.Initiator is BasePlayer)
            {
                var attacked = (BasePlayer)entity;
                var initiator = (BasePlayer)info.Initiator;
                if (attacked.userID == initiator.userID)
                {
                    return null;
                }
                var attackedGamePlayer = FindPlayerByUserId(attacked.userID);
                var initiatorGamePlayer = FindPlayerByUserId(initiator.userID);
                if (attackedGamePlayer != null && initiatorGamePlayer != null)
                {
                    initiator.Hurt(info);
                }
            }
            {
                // Override scientists damge and accuracy to players
                var initiator = info?.Initiator;
                if (initiator != null && initiator is Scientist && entity is BasePlayer)
                {
                    var data = initiator.GetComponent<ZombieData>();
                    if (data != null)
                    {
                        int rand = UnityEngine.Random.Range(1, 100);
                        float distance = Vector3.Distance(info.Initiator.transform.position, entity.transform.position);

                        var newAccuracy = data.Accuracy;
                        var newDamage = data.Damage;
                        if (distance > 100f)
                        {
                            newAccuracy = data.Accuracy / (distance / 100f);
                            newDamage = data.Damage / (distance / 100f);
                        }
                        //Puts("New damage & new accuracy: " + newDamage + ", " + newAccuracy + ", RAND: " + rand);
                        if (((int)newAccuracy) < rand)
                            return true;
                        info.damageTypes.ScaleAll(newDamage);
                    }
                }
            }
            {
                // Handle tank boss
                if (entity is BradleyAPC && info.Initiator != null && info.Initiator.ToPlayer() != null && _GameData.NPCPlayers.Contains(entity))
                {
                    //info.Initiator.ToPlayer().ChatMessage("Damage dealt to tank before: " + info.damageTypes.Total());
                    info.damageTypes.ScaleAll(info.damageTypes.IsMeleeType() ? 10f : 1000f);
                    //info.Initiator.ToPlayer().ChatMessage("Damage dealt to tank after : " + info.damageTypes.Total());
                    if (info.damageTypes.Total() >= entity.health)
                    {
                        var position = entity.transform.position;
                        // Handle tank death
                        var gibs = entity.GetComponent<TankController>().OnDeath();
                        // Clear fireballs and then gibs after timers
                        timer.Once(30f, () =>
                        {
                            var fires = new List<FireBall>();
                            Vis.Entities(position, 20f, fires);
                            fires.ForEach((fire) => fire.Kill());
                        });
                        timer.Once(60f, () => gibs.ForEach((gib) => gib.Kill()));
                        return true;
                    }
                }
            }
            return null;
        }

        void OnPlayerDropActiveItem(BasePlayer player, Item item)
        {
            timer.Once(_ItemDespawnTime, () => item?.RemoveFromWorld());
        }
        #endregion

        #region GameLogic
        private void HandleNpcLootChange(BaseEntity spawned)
        {
            if (spawned == null)
            {
                return;
            }
            if (spawned is NPCPlayerCorpse)
            {
                var corpse = spawned as NPCPlayerCorpse;
                corpse.ResetRemovalTime(30f);
                NextTick(() =>
                {
                    if (corpse == null || corpse.containers == null)
                        return;
                    List<Item> toDestroy = new List<Item>();
                    foreach (var item in corpse.containers[0].itemList)
                        toDestroy.Add(item);
                    foreach (var item in toDestroy)
                        item?.RemoveFromContainer();
                    corpse.containers[0].Clear();
                    corpse.containers[1].Clear();
                    corpse.containers[2].Clear();
                });
            }
        }

        private GamePlayer FindPlayerByUserId(ulong userId)
        {
            return _GameData.Players.Find((player) => player.Entity.userID == userId);
        }

        private void InitialiseGamePlayer(BasePlayer player)
        {
            NextTick(() =>
            {
                player.Teleport(_Config.GetSpawnPosition());
                player.inventory.Strip();
                player.StopWounded();
                player.metabolism.Reset();
                player.metabolism.calories.SetValue(player.metabolism.calories.max);
                player.metabolism.hydration.SetValue(player.metabolism.hydration.max);
                player.ChangeHealth(player.MaxHealth());
                player.inventory.Strip();
                plugins.Find("Kits")?.Call("GiveKit", player, _Config.StartingKitName);
            });
        }

        private bool DestroyEmptyGame()
        {
            if (_GameData.Players.Count == 0)
            {
                _GameData.Reset();
                return true;
            }
            return false;
        }

        private void GameUpdate()
        {
            // Remove any murderers from list that are dead or destroyed
            _GameData.NPCPlayers.RemoveAll((murderer) =>
                murderer == null ||
                murderer.IsDestroyed ||
                (murderer is NPCPlayer && ((NPCPlayer)murderer).IsDead()) ||
                (murderer is HTNPlayer && ((HTNPlayer)murderer).IsDead()));

            if (DestroyEmptyGame())
            {
                return;
            }

            switch (_GameData.State)
            {
                case GAME_STATE.WAITING_FOR_PLAYERS:
                    HandleWaitingForPlayers();
                    break;
                case GAME_STATE.COUNTDOWN_WAVE:
                    HandleCountdownWave();
                    break;
                case GAME_STATE.RUNNING_WAVE:
                    HandleRunningWave();
                    break;
                case GAME_STATE.ALL_PLAYERS_DEAD:
                    HandleAllPlayersDead();
                    break;
                case GAME_STATE.PLAYERS_WIN:
                    HandlePlayersWin();
                    break;
                default:
                    Puts("ERROR: Unknown state: " + _GameData.State);
                    break;
            }
        }

        private void HandleWaitingForPlayers()
        {
            if (_GameData.Players.Count >= _Config.RequiredPlayercount)
            {
                _GameData.State = GAME_STATE.COUNTDOWN_WAVE;
            }
        }

        private void HandleCountdownWave()
        {
            if (_GameData.Waiting)
            {
                var wave = _GameData.WaveSpawnList();
                var diff = DateTime.Now - _GameData.TimeWaitingFrom;
                if (diff.TotalSeconds >= wave.SecondsUntilNextWave)
                {
                    _GameData.State = GAME_STATE.RUNNING_WAVE;
                    _GameData.Waiting = false;
                }
                return;
            }
            _GameData.Waiting = true;
            _GameData.TimeWaitingFrom = DateTime.Now;
        }

        private void HandleAllPlayersDead()
        {
            if (_GameData.Waiting)
            {
                var diff = DateTime.Now - _GameData.TimeWaitingFrom;
                if (diff.TotalSeconds >= 10D)
                {
                    while (_GameData.Players.Count > 0)
                    {
                        GrapeGameManager?.Call("PlayerRemovedFromGameHook", _GameData.Players[0].Entity);
                        RemovePlayer(_GameData.Players[0].Entity);
                    }
                    _GameData.Reset();
                }
                return;
            }
            _GameData.Waiting = true;
            _GameData.TimeWaitingFrom = DateTime.Now;
        }

        private void HandlePlayersWin()
        {
            if (_GameData.Waiting)
            {
                var diff = DateTime.Now - _GameData.TimeWaitingFrom;
                if (diff.TotalSeconds >= 10D)
                {
                    while (_GameData.Players.Count > 0)
                    {
                        GrapeGameManager?.Call("PlayerRemovedFromGameHook", _GameData.Players[0].Entity);
                        RemovePlayer(_GameData.Players[0].Entity);
                    }
                    _GameData.Reset();
                }
                return;
            }
            _GameData.Waiting = true;
            _GameData.TimeWaitingFrom = DateTime.Now;
        }

        #region RunningWaveAndSpawningLogic
        private void HandleRunningWave()
        {
            if (_GameData.Waiting)
            {
                SpawnRemainingWaveToMaxCount();
                if (_GameData.NPCPlayers.Count == 0)
                {
                    _GameData.RewardWave();
                    _GameData.State = GAME_STATE.COUNTDOWN_WAVE;
                    _GameData.IncrementWave();
                    _GameData.Waiting = false;
                    _GameData.Players.ForEach((player) => player.ResetStats());
                }
                // If all players are wounded end the game
                else if (_GameData.Players.Find((player) => player.Entity != null && !player.Entity.IsWounded()) == null)
                {
                    _GameData.State = GAME_STATE.ALL_PLAYERS_DEAD;
                    _GameData.Waiting = false;
                }
                return;
            }
            _GameData.Waiting = true;
            SpawnWaveToMaxCount();
        }

        private void SpawnWaveToMaxCount()
        {
            var wave = _GameData.WaveSpawnList();
            var numPlayers = _GameData.Players.Count;
            var maxSpawn = wave.MaxSpawnAmount;
            var currentCount = _GameData.NPCPlayers.Count;
            var spawnLaterData = new List<SpawnLaterData>();
            foreach (var spawn in wave.Spawns)
            {
                var numberToSpawn = numPlayers * spawn.Amount;
                for (int i = 0; i < numberToSpawn; ++i)
                {
                    var location = spawn.SpawnLocations[UnityEngine.Random.Range(0, spawn.SpawnLocations.Count)];
                    // Handle special waves
                    if (spawn.State != GAME_STATE.RUNNING_WAVE)
                    {
                        if (HandleCustomWaveState(spawn, location))
                            continue;
                    }
                    // If we have hit the maximum amount that can be spawned at a single time, save the data for later
                    if (++currentCount >= maxSpawn)
                    {
                        spawnLaterData.Add(new SpawnLaterData
                        {
                            Location = location,
                            Prefab = spawn.Prefab,
                            Health = spawn.Health,
                            DisplayName = spawn.DisplayName,
                            MinScrap = spawn.MinScrap,
                            MaxScrap = spawn.MaxScrap,
                            Kit = spawn.Kit,
                            Damage = spawn.Damage,
                            Accuracy = spawn.Accuracy
                        });
                    }
                    else
                    {
                        SpawnZombie(spawn.Prefab, spawn.Health, location,
                            spawn.DisplayName, spawn.MinScrap, spawn.MaxScrap,
                            spawn.Kit, spawn.Damage, spawn.Accuracy);
                    }
                }
            }
            _GameData.SpawnLaterData = spawnLaterData;
        }

        private bool HandleCustomWaveState(WaveConfiguration.WaveSpawn spawn, Vector3 location)
        {
            if (spawn.State == GAME_STATE.TANK_BOSS_1)
            {
                /*_GameData.Players.ForEach((player) =>
                {
                    if (player.Entity == null)
                        return;
                    player.Entity.GiveItem(ItemManager.CreateByName("rocket.launcher", 1, 853494512));
                    player.Entity.GiveItem(ItemManager.CreateByName("ammo.rocket.basic", 4));
                    player.Entity.ChatMessage("You have been given a rocket launcher!");
                });*/
                SpawnTankBoss(spawn, location);
                return true;
            }
            return false;
        }

        private void SpawnRemainingWaveToMaxCount()
        {
            var maxSpawn = _GameData.WaveSpawnList().MaxSpawnAmount;
            var currentCount = _GameData.NPCPlayers.Count;
            // Loop without having a handle on the data
            while (_GameData.SpawnLaterData.Count > 0)
            {
                if (currentCount++ >= maxSpawn)
                {
                    break;
                }
                var data = _GameData.SpawnLaterData[0];
                SpawnZombie(data.Prefab, data.Health, data.Location,
                    data.DisplayName, data.MinScrap, data.MaxScrap,
                    data.Kit, data.Damage, data.Accuracy);
                _GameData.SpawnLaterData.RemoveAt(0);
            }
        }

        private class SpawnLaterData
        {
            public Vector3 Location;
            public string Prefab;
            public float Health;
            public string DisplayName;
            public int MinScrap;
            public int MaxScrap;
            public string Kit;
            public float Damage;
            public float Accuracy;
        }
        #endregion

        private void SpawnZombie(string prefab, float health, Vector3 spawnLocation, string displayName = "Zombie",
            int minScrap = 0, int maxScrap = 0, string kit = null, float damage = 0.25f, float accuracy = 1f)
        {
            Puts("Spawning: " + prefab);
            var entity = GameManager.server.CreateEntity(prefab, spawnLocation, Quaternion.identity, false);
            if (entity != null)
            {
                entity.enableSaving = false;
                entity.gameObject.AwakeFromInstantiate();
                entity.Spawn();
                if (entity is BasePlayer)
                {
                    var plr = (BasePlayer)entity;
                    plr.displayName = displayName;
                    var data = plr.gameObject.AddComponent<ZombieData>();
                    data.MinScrap = minScrap;
                    data.MaxScrap = maxScrap;
                    var npcAgex = entity.GetComponent<NPCPlayerApex>();
                    if (npcAgex)
                    {
                        npcAgex.Stats.AggressionRange = 200f;
                        npcAgex.Stats.VisionRange = 200f;
                        npcAgex.ChangeHealth(health);
                        npcAgex.startHealth = health;
                        npcAgex._maxHealth = health;
                        npcAgex.Stats.Hostility = 1f;
                        // Disable radio
                        npcAgex.RadioEffect = new GameObjectRef();
                        // Move to random players position constantly
                        if (plr is Scientist)
                        {
                            data.Damage = damage;
                            data.Accuracy = accuracy;
                        }
                    }
                    if (kit != null)
                    {
                        plr.inventory.Strip();
                        Kits?.Call("GiveKit", entity, kit);
                    }
                    if (entity is NPCPlayer || entity is NPCMurderer)
                    {
                        Timer _npcTimer = null;
                        _npcTimer = timer.Every(5f, () =>
                        {
                            if (plr == null || plr.IsDead())
                            {
                                _npcTimer.Destroy();
                                return;
                            }
                            var randomPlayer = _GameData.Players.Count == 0 ? null :
                                _GameData.Players[UnityEngine.Random.Range(0, _GameData.Players.Count)];
                            NPCPlayer scientist = entity as NPCPlayer;
                            NPCMurderer murderer = entity as NPCMurderer;
                            if (randomPlayer != null && scientist != null && scientist.NavAgent.isOnNavMesh)
                            {
                                scientist.NavAgent.SetDestination(randomPlayer.Entity.transform.position);
                            }
                            else if (randomPlayer != null && murderer != null && murderer.NavAgent.isOnNavMesh)
                            {
                                murderer.NavAgent.SetDestination(randomPlayer.Entity.transform.position);
                            }
                        });
                    }
                }
                else if (entity is HTNPlayer)
                {
                    var plr = (HTNPlayer)entity;
                    plr.AiDomain.Movement = Rust.Ai.HTN.HTNDomain.MovementRule.FreeMove;
                    plr.AiDomain.MovementRadius = 300f;
                    plr.health = health;
                    plr._maxHealth = health;
                    plr.startHealth = health;
                    plr.displayName = displayName;
                    plr.AiDefinition.Engagement.AggroRange = 200f;
                    plr.AiDefinition.Sensory.VisionRange = 200f;
                    if (kit != null)
                    {
                        plr.inventory.Strip();
                        Kits?.Call("GiveKit", entity, kit);
                    }
                }
                _GameData.NPCPlayers.Add(entity);
            }
        }

        public HashSet<ulong> GetUserIds()
        {
            var userIds = new HashSet<ulong>();
            foreach (var player in _GameData.Players)
            {
                userIds.Add(player.UserID);
            }
            return userIds;
        }
        #endregion

        #region TankBoss
        private void SpawnTankBoss(WaveConfiguration.WaveSpawn spawn, Vector3 location)
        {
            Puts("Spawning: " + spawn.Prefab);
            var entity = GameManager.server.CreateEntity(spawn.Prefab, location, Quaternion.identity, false);
            if (entity != null)
            {
                entity.enableSaving = false;
                entity.gameObject.AwakeFromInstantiate();
                entity.Spawn();
                if (entity is BradleyAPC)
                {
                    var tank = entity as BradleyAPC;
                    var health = spawn.Health * _GameData.Players.Count;
                    tank.InitializeHealth(health, health);
                    var controller = tank.gameObject.AddComponent<TankController>();
                    controller.MinScrapPerBox = spawn.MinScrap;
                    controller.MaxScrapPerBox = spawn.MaxScrap;
                    controller.ScrapScaling = _GameData.Players.Count;
                }
                _GameData.NPCPlayers.Add(entity);
            }
        }

        private class TankController : MonoBehaviour
        {
            private BradleyAPC entity;
            private TankWaypointController waypointController;

            private float MainGunMinDistance = 7f;
            private int MainGunShots = 1;
            private int GattlingGunShots = 8;
            private float MainGunCooldown = 3f;
            private float GattlingCooldown = 5f;
            private float gattlingFireRate = 0.06667f;
            private float mainGunFireRate = 0.25f;
            private int numMainGunFired;
            private int numGattlingFired;
            private float nextGattlingFireTime;
            private float nextMainGunFireTime;

            public int MinScrapPerBox = 12;
            public int MaxScrapPerBox = 24;
            public float ScrapScaling = 1f;
            public int NumLootBoxes = 10;

            private float lastLateUpdate;
            private Vector3 desiredTopTurretAimVector = Vector3.forward;
            private Vector3 desiredAimVector = Vector3.forward;

            private float currentSpeedZoneLimit;

            private BaseCombatEntity mainGunTarget;

            private void Awake()
            {
                entity = GetComponent<BradleyAPC>();
                // Disable normal tank AI
                entity.enabled = false;
                //entity.CancelInvoke(entity.UpdateTargetList);
                //entity.CancelInvoke(entity.UpdateTargetVisibilities);
                waypointController = new TankWaypointController();
            }

            public void FixedUpdate()
            {
                DoSimpleAI();
                DoPhysicsMove();
                DoWeapons();
                entity.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
            }

            public void LateUpdate()
            {
                float single = Time.time - lastLateUpdate;
                lastLateUpdate = Time.time;
                if (!entity.isServer)
                {
                    entity.turretAimVector = Vector3.Lerp(entity.turretAimVector, desiredAimVector, Time.deltaTime * 10f);
                }
                else
                {
                    float single1 = 2.09439516f;
                    entity.turretAimVector = Vector3.RotateTowards(entity.turretAimVector, desiredAimVector, single1 * single, 0f);
                }
                entity.AimWeaponAt(entity.mainTurret, entity.coaxPitch, entity.turretAimVector, -90f, 90f, 360f, null);
                entity.AimWeaponAt(entity.mainTurret, entity.CannonPitch, entity.turretAimVector, -90f, 7f, 360f, null);
                entity.topTurretAimVector = Vector3.Lerp(entity.topTurretAimVector, desiredTopTurretAimVector, Time.deltaTime * 5f);
                entity.AimWeaponAt(entity.topTurretYaw, entity.topTurretPitch, entity.topTurretAimVector, -360f, 360f, 360f, entity.mainTurret);
            }

            public void DoSimpleAI()
            {
                if (entity.isClient)
                {
                    return;
                }
                entity.SetFlag(BaseEntity.Flags.Reserved5, TOD_Sky.Instance.IsNight, false, true);
                if (entity.targetList.Count > 0)
                {
                    if (!entity.targetList[0].IsValid() || !entity.targetList[0].IsVisible())
                    {
                        mainGunTarget = null;
                    }
                    else
                    {
                        mainGunTarget = entity.targetList[0].entity as BaseCombatEntity;
                    }
                    entity.UpdateMovement_Hunt();
                }
                var destination = waypointController.UpdateAndGetDestination(entity);
                entity.SetDestination(destination);
                
                float single = Vector3.Distance(entity.transform.position, entity.destination);
                float single1 = Vector3.Distance(entity.transform.position, mainGunTarget != null ? mainGunTarget.transform.position : destination);
                if (single > entity.stoppingDist)
                {
                    Vector3 vector3 = BradleyAPC.Direction2D(entity.destination, entity.transform.position);
                    float single2 = Vector3.Dot(vector3, entity.transform.right);
                    float single3 = Vector3.Dot(vector3, entity.transform.right);
                    float single4 = Vector3.Dot(vector3, -entity.transform.right);
                    if (Vector3.Dot(vector3, -entity.transform.forward) <= single2)
                    {
                        entity.turning = Mathf.Clamp(single2 * 3f, -1f, 1f);
                    }
                    else if (single3 < single4)
                    {
                        entity.turning = -1f;
                    }
                    else
                    {
                        entity.turning = 1f;
                    }
                    float single5 = 1f - Mathf.InverseLerp(0f, 0.3f, Mathf.Abs(entity.turning));
                    float single6 = Mathf.InverseLerp(0.1f, 0.4f, Vector3.Dot(entity.transform.forward, Vector3.up));
                    entity.throttle = (0.1f + Mathf.InverseLerp(0f, 20f, single1) * 1f) * single5 + single6;
                }
                DoWeaponAiming();
            }

            public void DoWeaponAiming()
            {
                Vector3 aimPoint;
                Vector3 vector3;
                Vector3 vector31;
                var target = entity.targetList.Count > 0 ? entity.targetList[0].entity as BaseCombatEntity : null;
                if (target != null)
                {
                    aimPoint = entity.GetAimPoint(target) - entity.mainTurretEyePos.transform.position;
                    vector3 = aimPoint.normalized;
                }
                else
                {
                    vector3 = desiredAimVector;
                }
                desiredAimVector = vector3;
                BaseEntity item = null;
                if (entity.targetList.Count > 0)
                {
                    if (entity.targetList.Count > 1 && entity.targetList[1].IsValid() && entity.targetList[1].IsVisible())
                    {
                        item = entity.targetList[1].entity;
                    }
                    else if (entity.targetList[0].IsValid() && entity.targetList[0].IsVisible())
                    {
                        item = entity.targetList[0].entity;
                    }
                }
                if (item != null)
                {
                    aimPoint = entity.GetAimPoint(item) - entity.topTurretEyePos.transform.position;
                    vector31 = aimPoint.normalized;
                }
                else
                {
                    vector31 = transform.forward;
                }
                desiredTopTurretAimVector = vector31;
                entity.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
            }

            public void DoPhysicsMove()
            {
                if (entity.isClient)
                {
                    return;
                }
                Vector3 vector3 = entity.myRigidBody.velocity;
                entity.throttle = Mathf.Clamp(entity.throttle, -1f, 1f);
                entity.leftThrottle = entity.throttle;
                entity.rightThrottle = entity.throttle;
                if (entity.turning > 0f)
                {
                    entity.rightThrottle = -entity.turning;
                    entity.leftThrottle = entity.turning;
                }
                else if (entity.turning < 0f)
                {
                    entity.leftThrottle = entity.turning;
                    entity.rightThrottle = entity.turning * -1f;
                }
                float single = Vector3.Distance(entity.transform.position, entity.GetFinalDestination());
                float single1 = Vector3.Distance(entity.transform.position, entity.GetCurrentPathDestination());
                float single2 = 15f;
                if (single1 < 20f)
                {
                    float single3 = Vector3.Dot(entity.PathDirection(entity.currentPathIndex), entity.PathDirection(entity.currentPathIndex + 1));
                    float single4 = Mathf.InverseLerp(2f, 10f, single1);
                    float single5 = Mathf.InverseLerp(0.5f, 0.8f, single3);
                    single2 = 15f - 14f * ((1f - single5) * (1f - single4));
                }
                if (entity.patrolPath != null)
                {
                    float single6 = single2;
                    foreach (PathSpeedZone speedZone in entity.patrolPath.speedZones)
                    {
                        if (!speedZone.WorldSpaceBounds().Contains(entity.transform.position))
                        {
                            continue;
                        }
                        single6 = Mathf.Min(single6, speedZone.GetMaxSpeed());
                    }
                    currentSpeedZoneLimit = Mathf.Lerp(currentSpeedZoneLimit, single6, Time.deltaTime);
                    single2 = Mathf.Min(single2, currentSpeedZoneLimit);
                }
                if (entity.PathComplete())
                {
                    single2 = 0f;
                }
                //if (Global.developer > 1)
                //{
                //    Debug.Log(string.Concat(new object[] { "velocity:", vector3.magnitude, "max : ", single2 }));
                //}
                entity.brake = vector3.magnitude >= single2;
                entity.ApplyBrakes((entity.brake ? 1f : 0f));
                float single7 = entity.throttle;
                entity.leftThrottle = Mathf.Clamp(entity.leftThrottle + single7, -1f, 1f);
                entity.rightThrottle = Mathf.Clamp(entity.rightThrottle + single7, -1f, 1f);
                float single8 = Mathf.InverseLerp(2f, 1f, vector3.magnitude * Mathf.Abs(Vector3.Dot(vector3.normalized, entity.transform.forward)));
                float single9 = Mathf.Lerp(entity.moveForceMax, entity.turnForce, single8);
                float single10 = Mathf.InverseLerp(5f, 1.5f, vector3.magnitude * Mathf.Abs(Vector3.Dot(vector3.normalized, entity.transform.forward)));
                entity.ScaleSidewaysFriction(1f - single10);
                entity.SetMotorTorque(entity.leftThrottle, false, single9);
                entity.SetMotorTorque(entity.rightThrottle, true, single9);
                entity.impactDamager.damageEnabled = entity.myRigidBody.velocity.magnitude > 2f;
            }
            
            public void DoWeapons()
            {
                // Find target
                BaseCombatEntity target = null;
                foreach (var possibleEntity in entity.targetList)
                {
                    var possibleTarget = possibleEntity.entity.ToPlayer();
                    // Skip wounded players
                    if (possibleTarget != null && possibleTarget.IsWounded())
                    {
                        continue;
                    }
                    target = possibleTarget;
                    break;
                }
                // If no players try to target a npc
                if (target == null && entity.targetList.Count > 0)
                {
                    target = entity.targetList[0].entity as BaseCombatEntity;
                }
                if (target == null)
                {
                    return;
                }
                // Fire all weapons
                if (Vector3.Dot(entity.turretAimVector, (entity.GetAimPoint(target) - entity.mainTurretEyePos.transform.position).normalized) >= 0.99f)
                {
                    bool flag = entity.VisibilityTest(target);
                    float single = Vector3.Distance(target.transform.position, entity.transform.position);
                    if (Time.time > nextGattlingFireTime & flag && single <= 40f)
                    {
                        numGattlingFired++;
                        entity.FireGun(entity.GetAimPoint(target), 3f, true);
                        nextGattlingFireTime = Time.time + gattlingFireRate;
                        if (numGattlingFired >= GattlingGunShots)
                        {
                            nextGattlingFireTime = Time.time + GattlingCooldown;
                            numGattlingFired = 0;
                        }
                    }
                    if (single >= MainGunMinDistance & flag)
                    {
                        FireGunTest();
                    }
                }
            }

            public void FireGunTest()
            {
                if (Time.time < nextMainGunFireTime)
                {
                    return;
                }
                nextMainGunFireTime = Time.time + mainGunFireRate;
                numMainGunFired++;
                if (numMainGunFired >= MainGunShots)
                {
                    nextMainGunFireTime = Time.time + MainGunCooldown;
                    numMainGunFired = 0;
                }
                Vector3 modifiedAimConeDirection = AimConeUtil.GetModifiedAimConeDirection(2f, entity.CannonMuzzle.rotation * Vector3.forward, true);
                Vector3 cannonPitch = (entity.CannonPitch.transform.rotation * Vector3.back) + (entity.transform.up * -1f);
                Vector3 vector3 = cannonPitch.normalized;
                entity.myRigidBody.AddForceAtPosition(vector3 * entity.recoilScale, entity.CannonPitch.transform.position, ForceMode.Impulse);
                Effect.server.Run(entity.mainCannonMuzzleFlash.resourcePath, entity, StringPool.Get(entity.CannonMuzzle.gameObject.name), Vector3.zero, Vector3.zero, null, false);
                BaseEntity baseEntity = GameManager.server.CreateEntity(entity.mainCannonProjectile.resourcePath, entity.CannonMuzzle.transform.position, Quaternion.LookRotation(modifiedAimConeDirection), true);
                if (baseEntity == null)
                {
                    return;
                }
                ServerProjectile component = baseEntity.GetComponent<ServerProjectile>();
                if (component)
                {
                    component.InitializeVelocity(modifiedAimConeDirection * component.speed);
                }
                baseEntity.Spawn();
            }

            public List<ServerGib> OnDeath()
            {
                int minScrap = (int)(MinScrapPerBox * ScrapScaling);
                int maxScrap = (int)(MaxScrapPerBox * ScrapScaling);
                // Spawn loot boxes
                for (int i = 0; i < NumLootBoxes; i++)
                {
                    Vector3 onSphere = UnityEngine.Random.onUnitSphere;
                    BaseEntity lootCrate = GameManager.server.CreateEntity(entity.crateToDrop.resourcePath,
                        (entity.transform.position + new Vector3(0f, 1.5f, 0f)) + (onSphere * UnityEngine.Random.Range(2f, 3f)),
                        Quaternion.LookRotation(onSphere), true);
                    lootCrate.Spawn();

                    LootContainer lootContainer = lootCrate as LootContainer;
                    if (lootContainer)
                    {
                        lootContainer.inventory.itemList.Clear();
                        Item newItem = ItemManager.CreateByName("scrap", UnityEngine.Random.Range(minScrap, maxScrap));
                        newItem.MoveToContainer(lootContainer.inventory);
                        lootContainer.Invoke(new Action(lootContainer.RemoveMe), 120f);
                    }

                    Collider collider = lootCrate.GetComponent<Collider>();
                    Rigidbody rigidbody = lootCrate.gameObject.AddComponent<Rigidbody>();
                    rigidbody.useGravity = true;
                    rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                    rigidbody.mass = 2f;
                    rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
                    rigidbody.velocity = Vector3.zero + (onSphere * UnityEngine.Random.Range(1f, 3f));
                    rigidbody.angularVelocity = Vector3Ex.Range(-1.75f, 1.75f);
                    rigidbody.drag = 0.5f * (rigidbody.mass / 5f);
                    rigidbody.angularDrag = 0.2f * (rigidbody.mass / 5f);
                    
                    // fire on loot boxes
                    /*FireBall fireBall = GameManager.server.CreateEntity(entity.fireBall.resourcePath, lootCrate.transform.position, new Quaternion(), true) as FireBall;
                    if (fireBall)
                    {
                        fireBall.transform.position = lootCrate.transform.position;
                        fireBall.Spawn();
                        fireBall.GetComponent<Rigidbody>().isKinematic = true;
                        fireBall.GetComponent<Collider>().enabled = false;
                        fireBall.transform.parent = lootCrate.transform;
                    }
                    lootCrate.SendMessage("SetLockingEnt", fireBall.gameObject, SendMessageOptions.DontRequireReceiver);*/
                }
                // Spawn death effects (fire, body, debris) then destroy entity
                Effect.server.Run(entity.explosionEffect.resourcePath, entity.transform.position, Vector3.up, null, true);
                List<ServerGib> serverGibs = ServerGib.CreateGibs(entity.servergibs.resourcePath, entity.gameObject, entity.servergibs.Get().GetComponent<ServerGib>()._gibSource, Vector3.zero, 3f);
                for (int i = 0; i < 12 - entity.maxCratesToSpawn; i++)
                {
                    BaseEntity fireBall = GameManager.server.CreateEntity(entity.fireBall.resourcePath, entity.transform.position, entity.transform.rotation, true);
                    if (fireBall)
                    {
                        Vector3 onSphere = UnityEngine.Random.onUnitSphere;
                        fireBall.transform.position = (entity.transform.position + new Vector3(0f, 1.5f, 0f)) + (onSphere * UnityEngine.Random.Range(-4f, 4f));
                        Collider collider = fireBall.GetComponent<Collider>();
                        fireBall.Spawn();
                        fireBall.SetVelocity(Vector3.zero + (onSphere * UnityEngine.Random.Range(3, 10)));
                        foreach (ServerGib serverGib in serverGibs)
                            Physics.IgnoreCollision(collider, serverGib.GetCollider(), true);
                    }
                }
                if (entity != null && !entity.IsDestroyed)
                    entity.Kill(BaseNetworkable.DestroyMode.Gib);
                return serverGibs;
            }
        }
        #endregion

        #region TankWaypointController
        private class TankWaypointController
        {
            private int WaypointIndex;
            private Waypoint[] Waypoints = new Waypoint[]
            {
                // 0 Start at the top gate, head towards first truck
                new Waypoint()
                {
                    Destination = new Vector3(316.077f, 0.9093992f, -473.2338f)
                },
                // 1 Navigate around the truck
                new Waypoint()
                {
                    Destination = new Vector3(323.5825f, 1.008629f, -471.2044f)
                },
                // 2 forwards before heading to next point to stop clipping
                new Waypoint()
                {
                    Destination = new Vector3(323.395f, 1.09708f, -462.1819f)
                },
                // 3 Head towards the first turning
                new Waypoint()
                {
                    Destination = new Vector3(316.0526f, 0.9071671f, -447.1352f)
                },
                // 4 Down the turning through the middle of the map
                new Waypoint()
                {
                    Destination = new Vector3(335.4796f, 1.088596f, -446.8645f)
                },
                // 5 navigate around a truck
                new Waypoint()
                {
                    Destination = new Vector3(341.1654f, 1.096403f, -436.6854f),
                },
                // 6 another step to stop clipping, just past the truck through the gap
                new Waypoint()
                {
                    Destination = new Vector3(352.08249f, 0.8995048f, -438.5835f)
                },
                // 7 progress towards bottom gate away from middle of road
                new Waypoint()
                {
                    Destination = new Vector3(365.126f, 0.9017781f, -450.3417f)
                },
                // 8 near bottom gate
                new Waypoint()
                {
                    Destination = new Vector3(367.0446f, 0.9100981f, -472.2078f)
                },
                // 9 Back in front of the first truck we passed, reset route
                new Waypoint()
                {
                    Destination = new Vector3(317.7645f, 0.9015116f, -460.0943f),
                    NextWaypoint = 3
                }
            };

            public Vector3 UpdateAndGetDestination(BradleyAPC entity)
            {
                var waypoint = Waypoints[WaypointIndex];
                if (Vector3.Distance(entity.transform.position, waypoint.Destination) <= entity.stoppingDist)
                {
                    if (waypoint.NextWaypoint == -1)
                    {
                        if (++WaypointIndex >= Waypoints.Length)
                        {
                            WaypointIndex = 0;
                        }
                    }
                    else
                    {
                        WaypointIndex = waypoint.NextWaypoint;
                    }
                }
                return waypoint.Destination;
            }

            private class Waypoint
            {
                public Vector3 Destination;
                public int NextWaypoint = -1;
            }
        }
        #endregion

        #region GamePlayer class
        class GamePlayer
        {
            public readonly ulong UserID;
            public readonly string DisplayName;
            public readonly BasePlayer Entity;
            public int Kills { private set; get; }
            public int Scrap { private set; get; }
            public int Deaths { private set; get; }

            private Vector3 homePosition;

            public GamePlayer(BasePlayer player)
            {
                UserID = player.userID;
                DisplayName = player.displayName;
                Entity = player;
                homePosition = player.transform.position;
                Kills = 0;
                Scrap = 0;
                Deaths = 0;
            }

            public void SendHome()
            {
                if (Entity == null)
                {
                    return;
                }
                Entity.inventory.Strip();
                ResetStats();
                Entity.Teleport(homePosition);
            }

            public void ResetStats()
            {
                Entity.StopWounded();
                Entity.metabolism.Reset();
                Entity.metabolism.calories.SetValue(Entity.metabolism.calories.max);
                Entity.metabolism.hydration.SetValue(Entity.metabolism.hydration.max);
                Entity.ChangeHealth(Entity.MaxHealth());
            }

            public void AddKill() => ++Kills;

            public void AddDeath() => ++Deaths;

            public void UpdateScrapCount()
            {
                var inventory = Entity.inventory;
                var amount = 0;
                var mainItemsFound = inventory.containerMain.FindItemsByItemName("scrap");
                if (mainItemsFound != null)
                {
                    amount += mainItemsFound.amount;
                }
                mainItemsFound = inventory.containerBelt.FindItemsByItemName("scrap");
                if (mainItemsFound != null)
                {
                    amount += mainItemsFound.amount;
                }
                Scrap = amount;
            }

            public void Destroy()
            {

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
                StartingKitName = "ZombieStart",
                SpawnPosition = new List<float>(new float[] { 315.631f, 3.77f, -386.8645f }),
                RequiredPlayercount = 1
            };
        }

        public class GameConfig
        {
            public string GameName = "";
            public string StartingKitName = "";
            public List<float> SpawnPosition = new List<float>();
            public int RequiredPlayercount;
            
            public GameConfig()
            {
            }

            public Vector3 GetSpawnPosition() => new Vector3(SpawnPosition[0], SpawnPosition[1], SpawnPosition[2]);
        }
        #endregion

        #region RunningGameData
        private class RunningGameData
        {
            public List<GamePlayer> Players { get; } = new List<GamePlayer>();
            public List<BaseEntity> NPCPlayers { get; } = new List<BaseEntity>();
            public List<SpawnLaterData> SpawnLaterData = new List<SpawnLaterData>();
            public GAME_STATE State;
            private int _Wave;
            private WaveConfiguration _WaveConfig;
            public bool Waiting;
            public DateTime TimeWaitingFrom;
            private GameConfig _GameConfig;

            public RunningGameData(WaveConfiguration waveConfig, GameConfig gameConfig)
            {
                _WaveConfig = waveConfig;
                _GameConfig = gameConfig;
                Reset();
            }

            public void Reset()
            {
                NPCPlayers.ForEach(npc => npc.Kill());
                Waiting = false;
                _Wave = 0;
                State = GAME_STATE.WAITING_FOR_PLAYERS;
            }

            public bool DebugState(int wave)
            {
                wave -= 1;
                bool validWave = wave < _WaveConfig.WaveSpawns.Count;
                if (validWave)
                {
                    _Wave = wave;
                }
                return validWave;
            }

            public int Wave() => _Wave;
            public int FriendlyWave() => _Wave + 1;

            public int IncrementWave()
            {
                var allWavesDefeated = FriendlyWave() >= _WaveConfig.WaveSpawns.Count;
                if (allWavesDefeated)
                {
                    Waiting = false;
                    State = GAME_STATE.PLAYERS_WIN;
                }
                return allWavesDefeated ? _Wave : ++_Wave;
            }

            public WaveConfiguration.WaveSpawnList WaveSpawnList() => _WaveConfig.WaveSpawns[Wave()];

            public void RewardWave()
            {
                foreach (var player in Players)
                {
                    if (player.Entity == null || player.Entity.IsWounded())
                    {
                        continue;
                    }
                    foreach (var itemRewardStr in WaveSpawnList().ItemNameRewards)
                    {
                        var itemName = itemRewardStr;
                        var amount = 1;
                        var skin = 0UL;
                        if (itemRewardStr.IndexOf('|') > 0)
                        {
                            var parts = itemRewardStr.Split('|');
                            itemName = parts[0];
                            int.TryParse(parts[1], out amount);
                            if (parts.Length > 2)
                            {
                                ulong.TryParse(parts[2], out skin);
                            }
                        }
                        var reward = ItemManager.CreateByName(itemName, amount, skin);
                        if (reward != null)
                        {
                            var name = itemName;//.Contains(".") ? itemName.Substring(itemName.LastIndexOf('.') + 1) : itemName;
                            var amountStr = amount == 1 ? "a" : amount.ToString();
                            player.Entity.ChatMessage($"You have been rewarded with {amountStr} {name} for beating wave {FriendlyWave()}!");
                            player.Entity.GiveItem(reward);
                        }
                    }
                }
            }

            public double ProgressPercent()
            {
                var spawn = WaveSpawnList().Spawns[0];
                if (spawn.State == GAME_STATE.TANK_BOSS_1)
                {
                    if (NPCPlayers.Count > 0 && NPCPlayers[0] is BradleyAPC)
                    {
                        var tank = NPCPlayers[0] as BradleyAPC;
                        float percentHealth = tank.IsAlive() ? (tank.health / spawn.Health) * 100f : 0f;
                        return percentHealth;
                    }
                }
                return 0D;
            }

            public int TimeRemaining()
            {
                if (!Waiting)
                {
                    return 0;
                }
                var elapsedTime = DateTime.Now - TimeWaitingFrom;
                var requiredTime = State == GAME_STATE.ALL_PLAYERS_DEAD ? 10f : WaveSpawnList().SecondsUntilNextWave;
                var timeLeft = requiredTime - elapsedTime.TotalSeconds;
                return timeLeft < 0 ? 0 : (int) timeLeft;
            }

            public string ObjectiveText()
            {
                var objectiveText = "";
                switch (State)
                {
                    case GAME_STATE.WAITING_FOR_PLAYERS:
                        objectiveText = $"Waiting for players {Players.Count} / {_GameConfig.RequiredPlayercount}";
                        break;
                    case GAME_STATE.COUNTDOWN_WAVE:
                        objectiveText = "Time until wave " + FriendlyWave();
                        break;
                    case GAME_STATE.RUNNING_WAVE:
                        if (WaveSpawnList().Spawns[0].State == GAME_STATE.TANK_BOSS_1)
                            objectiveText = $"1 zombie tank remaining on wave {FriendlyWave()}";
                        else
                            objectiveText = $"{NPCPlayers.Count + SpawnLaterData.Count} zombies remaining on wave {FriendlyWave()}";
                        break;
                    case GAME_STATE.ALL_PLAYERS_DEAD:
                        objectiveText = "Game ending: all players have been killed";
                        break;
                    case GAME_STATE.PLAYERS_WIN:
                        objectiveText = "Game ending: all waves have been defeated";
                        break;
                }
                return objectiveText;
            }
        }
        #endregion

        #region WaveSpawnData
        private class WaveConfiguration
        {
            public List<WaveSpawnList> WaveSpawns;

            public WaveConfiguration()
            {
                var spawnLocations = new List<Vector3>(new Vector3[]
                {
                    new Vector3(359.9564f, 1.098177f, -404.7091f),
                    new Vector3(329.6436f, 1.007179f, -411.355f),
                    new Vector3(274.4064f, 1.068121f, -427.4938f),
                    new Vector3(318.4145f, 0.8991073f, -500.3196f),
                    new Vector3(312.2376f, 0.894487f, -507.4466f),
                    new Vector3(369.9795f, 0.910639f, -501.6995f),
                    new Vector3(362.4135f, 1.051751f, -505.571f),
                    new Vector3(412.4794f, 1.06821f, -453.4566f),
                    new Vector3(347.7139f, 1.00712f, -449.9561f),
                    new Vector3(341.8737f, 1.007139f, -422.9041f),
                    new Vector3(385.5122f, 0.8629131f, -404.7485f),
                    new Vector3(274.6686f, 1.007311f, -450.486f),
                    new Vector3(376.128f, 1.007181f, -446.6971f),
                    new Vector3(355.1196f, 1.007139f, -465.9691f)
                });
                WaveSpawns = new List<WaveSpawnList>(new WaveSpawnList[]
                {
                    // Wave 1
                    new WaveSpawnList(30f, 10, new WaveSpawn[]
                    {
                        new WaveSpawn()
                        {
                            DisplayName = "Attack Zombie",
                            Amount = 2,
                            Prefab = _MurdererPrefab,
                            Health = 30f,
                            MinScrap = 2,
                            MaxScrap = 7,
                            Kit = "ZombieMelee",
                            SpawnLocations = spawnLocations
                        },
                        new WaveSpawn()
                        {
                            DisplayName = "Attack Zombie",
                            Amount = 2,
                            Prefab = _MurdererPrefab,
                            Health = 30f,
                            MinScrap = 2,
                            MaxScrap = 7,
                            Kit = "ZombieMelee2",
                            SpawnLocations = spawnLocations
                        },
                        new WaveSpawn()
                        {
                            DisplayName = "Attack Zombie",
                            Amount = 2,
                            Prefab = _MurdererPrefab,
                            Health = 30f,
                            MinScrap = 2,
                            MaxScrap = 7,
                            Kit = "ZombieBasicSword",
                            SpawnLocations = spawnLocations
                        }
                    },
                    new string[]
                    {
                        "crossbow|1|856029421"
                    }),
                    // Wave 2
                    new WaveSpawnList(30f, 10, new WaveSpawn[]
                    {
                        new WaveSpawn()
                        {
                            Amount = 4,
                            Prefab = _MurdererPrefab,
                            Health = 50f,
                            MinScrap = 10,
                            MaxScrap = 15,
                            DisplayName = "Elite Attack Zombie",
                            Kit = "ZombieChainsaw",
                            SpawnLocations = spawnLocations
                        },
                        new WaveSpawn()
                        {
                            Amount = 3,
                            Prefab = _MurdererPrefab,
                            Health = 40f,
                            MinScrap = 3,
                            MaxScrap = 15,
                            DisplayName = "Eoka Zombie",
                            Kit = "ZombieEoka",
                            SpawnLocations = spawnLocations
                        },
                        new WaveSpawn()
                        {
                            Amount = 4,
                            Prefab = _MurdererPrefab,
                            Health = 40f,
                            MinScrap = 10,
                            MaxScrap = 15,
                            DisplayName = "Miner Zombie",
                            Kit = "ZombieMiner",
                            SpawnLocations = spawnLocations
                        }
                    },
                    new string[]
                    {
                        "pistol.revolver|1|809865395",
                        "largemedkit"
                    }),
                    // Wave 3
                    new WaveSpawnList(30f, 10, new WaveSpawn[]
                    {
                        new WaveSpawn()
                        {
                            Amount = 5,
                            Prefab = _ScientistPrefab,
                            Health = 40f,
                            MinScrap = 10,
                            MaxScrap = 13,
                            DisplayName = "Shotgun Zombie",
                            Kit = "ZombieShotgunBasic",
                            Damage = 0.25f,
                            Accuracy = 50f,
                            SpawnLocations = spawnLocations
                        },
                        new WaveSpawn()
                        {
                            DisplayName = "Attack Zombie",
                            Amount = 2,
                            Prefab = _MurdererPrefab,
                            Health = 10f,
                            MinScrap = 1,
                            MaxScrap = 5,
                            Kit = "ZombieMelee2",
                            SpawnLocations = spawnLocations
                        },
                        new WaveSpawn()
                        {
                            Amount = 2,
                            Prefab = _MurdererPrefab,
                            Health = 50f,
                            MinScrap = 10,
                            MaxScrap = 15,
                            DisplayName = "Elite Attack Zombie",
                            Kit = "ZombieChainsaw",
                            SpawnLocations = spawnLocations
                        },
                        new WaveSpawn()
                        {
                            Amount = 1,
                            Prefab = _MurdererPrefab,
                            Health = 40f,
                            MinScrap = 3,
                            MaxScrap = 15,
                            DisplayName = "Eoka Zombie",
                            Kit = "ZombieEoka",
                            SpawnLocations = spawnLocations
                        },
                        new WaveSpawn()
                        {
                            Amount = 2,
                            Prefab = _MurdererPrefab,
                            Health = 40f,
                            MinScrap = 10,
                            MaxScrap = 15,
                            DisplayName = "Miner Zombie",
                            Kit = "ZombieMiner",
                            SpawnLocations = spawnLocations
                        }
                    },
                    new string[]
                    {
                        "ammo.pistol|30"
                    }),
                    // Wave 4
                    new WaveSpawnList(30f, 10, new WaveSpawn[]
                    {
                        new WaveSpawn()
                        {
                            Amount = 5,
                            Prefab = _ScientistPrefab,
                            Health = 40f,
                            MinScrap = 10,
                            MaxScrap = 13,
                            DisplayName = "Shotgun Zombie",
                            Kit = "ZombieShotgunBasic",
                            Damage = 0.25f,
                            Accuracy = 50f,
                            SpawnLocations = spawnLocations
                        },
                        new WaveSpawn()
                        {
                            DisplayName = "Pistol Zombie",
                            Amount = 5,
                            Prefab = _ScientistPrefab,
                            Health = 30f,
                            MinScrap = 10,
                            MaxScrap = 13,
                            Kit = "ZombieGunBasic",
                            Damage = 0.25f,
                            Accuracy = 12f,
                            SpawnLocations = spawnLocations
                        }
                    },
                    new string[]
                    {
                        "shotgun.waterpipe|1|832764933",
                        "ammo.handmade.shell|4"
                    }),
                    // Wave 5
                    new WaveSpawnList(30f, 6, new WaveSpawn[]
                    {
                        new WaveSpawn()
                        {
                            Amount = 5,
                            Prefab = _ScientistPrefab,
                            Health = 20f,
                            MinScrap = 10,
                            MaxScrap = 15,
                            Damage = 0.25f,
                            Accuracy = 50f,
                            DisplayName = "Zombie Sniper",
                            Kit = "ZombieSniper",
                            SpawnLocations = spawnLocations
                        },
                        new WaveSpawn()
                        {
                            Amount = 1,
                            Prefab = _ScientistPrefab,
                            Health = 125f,
                            MinScrap = 18,
                            MaxScrap = 42,
                            Damage = 0.25f,
                            Accuracy = 33f,
                            DisplayName = "Hulk Zombie",
                            Kit = "ZombieHulk",
                            SpawnLocations = spawnLocations
                        }
                    },
                    new string[]
                    {
                        "weapon.mod.simplesight",
                        "syringe.medical|2"
                    }),
                    // Wave 6
                    new WaveSpawnList(30f, 5, new WaveSpawn[]
                    {
                        new WaveSpawn()
                        {
                            Amount = 6,
                            Prefab = _ScientistPrefab,
                            Health = 25f,
                            MinScrap = 10,
                            MaxScrap = 15,
                            Damage = 0.25f,
                            Accuracy = 20f,
                            DisplayName = "Gunner Zombie",
                            Kit = "ZombieSmg",
                            SpawnLocations = spawnLocations
                        },
                        new WaveSpawn()
                        {
                            Amount = 2,
                            Prefab = _ScientistPrefab,
                            Health = 125f,
                            MinScrap = 18,
                            MaxScrap = 42,
                            Damage = 0.25f,
                            Accuracy = 33f,
                            DisplayName = "Hulk Zombie",
                            Kit = "ZombieHulk",
                            SpawnLocations = spawnLocations
                        }
                    },
                    new string[]
                    {
                    }),
                    // Wave 7, Tank boss 1
                    new WaveSpawnList(10f, 1, new WaveSpawn []
                    {
                        new WaveSpawn()
                        {
                            DisplayName = "Zombie Tank",
                            Amount = 1,
                            Prefab = _TankPrefab,
                            Health = 135f,
                            MinScrap = 12,
                            MaxScrap = 24,
                            State = GAME_STATE.TANK_BOSS_1,
                            SpawnLocations = new List<Vector3>(new Vector3[]
                            {
                                new Vector3(316.0925f, 0.91057f, -505.0217f)
                            })
                        }
                    },
                    new string[]
                    {
                    }),
                    // Wave 8
                    new WaveSpawnList(60f, 10, new WaveSpawn []
                    {
                        new WaveSpawn()
                        {
                            Amount = 6,
                            Prefab = _ScientistPrefab,
                            Health = 60f,
                            MinScrap = 10,
                            MaxScrap = 15,
                            Damage = 0.25f,
                            Accuracy = 20f,
                            DisplayName = "Gunner Zombie",
                            Kit = "ZombieSmg",
                            SpawnLocations = spawnLocations
                        },
                        new WaveSpawn()
                        {
                            Amount = 6,
                            Prefab = _ScientistPrefab,
                            Health = 35f,
                            MinScrap = 10,
                            MaxScrap = 15,
                            Damage = 0.25f,
                            Accuracy = 50f,
                            DisplayName = "Zombie Sniper",
                            Kit = "ZombieSniper",
                            SpawnLocations = spawnLocations
                        },
                        new WaveSpawn()
                        {
                            Amount = 10,
                            Prefab = _MurdererPrefab,
                            Health = 40f,
                            MinScrap = 3,
                            MaxScrap = 4,
                            DisplayName = "Eoka Zombie",
                            Kit = "ZombieEoka",
                            SpawnLocations = spawnLocations
                        }
                    },
                    new string[]
                    {
                    })
                });
            }

            public class WaveSpawnList
            {
                public float SecondsUntilNextWave;
                public List<WaveSpawn> Spawns;
                public List<string> ItemNameRewards;
                public int MaxSpawnAmount;

                public WaveSpawnList(float secondsUntilNextWave, int maxSpawnAmount, WaveSpawn[] SpawnData, string[] itemNameRewards)
                {
                    Spawns = new List<WaveSpawn>(SpawnData);
                    ItemNameRewards = new List<string>(itemNameRewards);
                    SecondsUntilNextWave = secondsUntilNextWave;
                    MaxSpawnAmount = maxSpawnAmount;
                }
            }

            public class WaveSpawn
            {
                public int Amount;
                public string Prefab;
                public float Health;
                public int MinScrap;
                public int MaxScrap;
                public List<Vector3> SpawnLocations;
                public string Kit;
                public string DisplayName;
                public float Damage;
                public float Accuracy;
                public GAME_STATE State = GAME_STATE.RUNNING_WAVE;
            }
        }
        #endregion

        #region Scoreboard
        public string GenerateScoreboardString()
        {
            var players = new List<GamePlayer>(_GameData.Players.Count);
            foreach (var player in _GameData.Players)
            {
                player.UpdateScrapCount();
                players.Add(player);
            }
            players.Sort(new ScoreboardComparer());

            int playersToDisplay = players.Count < 7 ? players.Count : 7;
            int shownPlayers = 0;
            StringBuilder str = new StringBuilder();
            str.Append("Top " + playersToDisplay + " players in " + _GameName + "\n");
            str.Append("Name        | Scrap | Kills | Deaths \n");
            for (var i = players.Count - 1; i >= 0; --i)
            {
                if (shownPlayers++ == playersToDisplay)
                {
                    break;
                }
                GamePlayer player = players[i];
                str.Append(string.Format(
                    "{3,-11} | {0,-5} | {1,-5} | {2,-6} \n",
                    player.Scrap.ToString(),
                    player.Kills.ToString(),
                    player.Deaths.ToString(),
                    player.DisplayName));
            }
            return str.ToString();
        }

        class ScoreboardComparer : IComparer<GamePlayer>
        {
            public int Compare(GamePlayer x, GamePlayer y)
            {
                if (x.Scrap > y.Scrap)
                {
                    return 1;
                }
                else if (x.Scrap < y.Scrap)
                {
                    return -1;
                }
                return 0;
            }
        }

        private static readonly string ScoreboardPrefix = "Scoreboard";
        private void UpdateScoreboard()
        {
            if (_GameData.Players.Count == 0)
            {
                return;
            }
            var scoreboardName = ScoreboardPrefix + _GameName;
            var scoreboardString = GenerateScoreboardString();
            var element = ScoreBoard?.Call(
                "GetScoreboardElement",
                scoreboardName,
                scoreboardString);
            if (element == null)
                return;
            var userIds = GetUserIds();
            ScoreBoard?.Call(
                "RenderScoreboardToPlayers",
                element,
                scoreboardName,
                userIds);
        }

        private void UpdateTimeAndObjective()
        {
            if (_GameData.Players.Count == 0)
            {
                return;
            }
            var timeLeft = _GameData.TimeRemaining();
            var objectiveText = _GameData.ObjectiveText();
            var progress = _GameData.ProgressPercent();
            foreach (var player in _GameData.Players)
            {
                GameDefenceCui?.Call("DisplayGUIExternal", player.Entity, timeLeft, objectiveText, progress);
            }
        }
        #endregion

        #region ZombieData
        public class ZombieData : MonoBehaviour
        {
            public int MinScrap;
            public int MaxScrap;
            public float Accuracy;
            public float Damage;
        }
        #endregion
    }
}

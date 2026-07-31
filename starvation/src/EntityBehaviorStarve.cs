using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Starvation
{
    public class EntityBehaviorStarve : EntityBehavior
    {
        private const double DefaultEntityAge = 25;
        private const double InitialEnergyReserves = 4000;
        private const double StarveThresholdMild = -7500;
        private const double StarveThresholdModerate = -45000;
        private const double StarveThresholdSevere = -165000;
        private const double StarveThresholdExtreme = -450000;
        private const double DeathThreshold = -510000;

        private const string AccountingDayAttribute =
            "starvationAccountingDay";
        private const string DailyCaloriesConsumedAttribute =
            "dailyCaloriesConsumed";
        private const string DailyCaloriesBurnedAttribute =
            "dailyCaloriesBurned";
        private const string DailyMetGameSecondsAttribute =
            "dailyMetGameSeconds";
        private const string DailyTrackedGameSecondsAttribute =
            "dailyTrackedGameSeconds";
        private const string DailyAverageMetsAttribute =
            "dailyAverageMets";
        private const string EstimatedDailyCaloriesAttribute =
            "estimatedDailyCalories";
        private const string AllowanceDailyCaloriesAttribute =
            "allowanceDailyCalories";
        private const string DailyCalorieLimitAttribute =
            "dailyCalorieLimit";
        private const string LastDailyWeightChangeAttribute =
            "lastDailyWeightChangeKg";
        private const string PreviousDayCaloriesConsumedAttribute =
            "previousDayCaloriesConsumed";
        private const string PreviousDayCaloriesBurnedAttribute =
            "previousDayCaloriesBurned";

        private long serverListenerId;
        private long serverListenerSlowId;
        private double heatIndexTemp = 15;
        private bool fastTickFaultLogged;
        private bool slowTickFaultLogged;

        // Energy expenditure and food intake are accumulated here and committed
        // to the synchronized reserve only at the configured interval.
        private double pendingEnergyDeltaKilojoules;
        private double pendingCaloriesBurned;
        private double pendingMetGameSeconds;
        private double pendingTrackedGameSeconds;
        private double lastEnergyCommitTotalHours = double.NaN;

        public double energyReserves
        {
            get => entity.WatchedAttributes.GetDouble(
                "energyReserves",
                InitialEnergyReserves);
            set
            {
                entity.WatchedAttributes.SetDouble(
                    "energyReserves",
                    value);
                entity.WatchedAttributes.MarkPathDirty(
                    "energyReserves");
            }
        }

        public double bodyWeight
        {
            get => entity.WatchedAttributes.GetDouble(
                "bodyWeight",
                ModSystemStarvation.HealthyWeight(entity));
            set
            {
                entity.WatchedAttributes.SetDouble(
                    "bodyWeight",
                    value);
                entity.WatchedAttributes.MarkPathDirty(
                    "bodyWeight");
            }
        }

        public double ageInYears
        {
            get => entity.WatchedAttributes.GetDouble(
                "ageInYears",
                DefaultEntityAge);
            set
            {
                entity.WatchedAttributes.SetDouble(
                    "ageInYears",
                    value);
                entity.WatchedAttributes.MarkPathDirty(
                    "ageInYears");
            }
        }

        public double currentMETs
        {
            get => entity.WatchedAttributes.GetDouble(
                "currentMETs",
                1);
            set
            {
                entity.WatchedAttributes.SetDouble(
                    "currentMETs",
                    value);
                entity.WatchedAttributes.MarkPathDirty(
                    "currentMETs");
            }
        }

        private double DailyCaloriesConsumed
        {
            get => entity.WatchedAttributes.GetDouble(
                DailyCaloriesConsumedAttribute,
                0);
            set => SetSyncedDouble(
                DailyCaloriesConsumedAttribute,
                value);
        }

        private double DailyCaloriesBurned
        {
            get => entity.WatchedAttributes.GetDouble(
                DailyCaloriesBurnedAttribute,
                0);
            set => SetSyncedDouble(
                DailyCaloriesBurnedAttribute,
                value);
        }

        private double DailyMetGameSeconds
        {
            get => entity.WatchedAttributes.GetDouble(
                DailyMetGameSecondsAttribute,
                0);
            set => SetSyncedDouble(
                DailyMetGameSecondsAttribute,
                value);
        }

        private double DailyTrackedGameSeconds
        {
            get => entity.WatchedAttributes.GetDouble(
                DailyTrackedGameSecondsAttribute,
                0);
            set => SetSyncedDouble(
                DailyTrackedGameSecondsAttribute,
                value);
        }

        private double DailyCalorieLimit
        {
            get => entity.WatchedAttributes.GetDouble(
                DailyCalorieLimitAttribute,
                0);
            set => SetSyncedDouble(
                DailyCalorieLimitAttribute,
                value);
        }

        public EntityBehaviorStarve(Entity entity) : base(entity)
        {
        }

        public override void Initialize(
            EntityProperties properties,
            JsonObject attributes)
        {
            base.Initialize(properties, attributes);
        }

        public override void AfterInitialized(bool onFirstSpawn)
        {
            base.AfterInitialized(onFirstSpawn);
            StartServerListeners();
        }

        private void StartServerListeners()
        {
            if (entity?.World?.Side != EnumAppSide.Server)
            {
                return;
            }

            if (serverListenerId == 0)
            {
                serverListenerId =
                    entity.World.RegisterGameTickListener(
                        ServerTick250,
                        250,
                        2000);
            }

            if (serverListenerSlowId == 0)
            {
                serverListenerSlowId =
                    entity.World.RegisterGameTickListener(
                        ServerTickSlow,
                        5000,
                        2000);
            }
        }

        public override void OnReceivedClientPacket(
            IServerPlayer player,
            int packetid,
            byte[] data,
            ref EnumHandling handled)
        {
            if (packetid != ModSystemStarvation.PacketIdMets)
            {
                base.OnReceivedClientPacket(
                    player,
                    packetid,
                    data,
                    ref handled);
                return;
            }

            if (entity?.WatchedAttributes == null)
            {
                handled = EnumHandling.Handled;
                return;
            }

            try
            {
                double receivedMets =
                    SerializerUtil.Deserialize<double>(data);

                currentMETs = Math.Clamp(
                    receivedMets,
                    0.5,
                    20);
            }
            catch (Exception exception)
            {
                entity.Api?.Logger.Warning(
                    "[Starvation] Ignored invalid MET packet: {0}",
                    exception.Message);
            }

            handled = EnumHandling.Handled;
        }

        private void ServerTickSlow(float deltaTime)
        {
            try
            {
                ServerTickSlowCore(deltaTime);
            }
            catch (Exception exception)
            {
                if (!slowTickFaultLogged)
                {
                    slowTickFaultLogged = true;
                    entity?.Api?.Logger.Error(
                        "[Starvation] Slow server tick disabled for " +
                        "this entity after an error: {0}",
                        exception);
                }
            }
        }

        private void ServerTickSlowCore(float deltaTime)
        {
            if (!TryGetReadyGameMode(out EnumGameMode gameMode))
            {
                return;
            }

            EnsureDailyAccounting();
            SyncClientDisplayValues();

            if (gameMode == EnumGameMode.Creative ||
                gameMode == EnumGameMode.Spectator)
            {
                ResetStarvationEffects();
                ResetHunger(true);
                return;
            }

            double ambientTemperature =
                ModSystemStarvation.GetTemperatureAtEntity(entity);
            double humidity =
                ModSystemStarvation.GetHumidityAtEntity(entity);

            heatIndexTemp =
                ModSystemStarvation.HeatIndexTemperature(
                    ambientTemperature,
                    humidity);

            UpdateDailyEstimates();
            ResetHunger();

            EntityBehaviorHealth health =
                entity.GetBehavior<EntityBehaviorHealth>();

            if (health != null)
            {
                health.SetMaxHealthModifiers(
                    "starvationMod",
                    (float)-MaxHealthPenalty());

                health.SetMaxHealthModifiers(
                    "nutrientHealthMod",
                    0f);
            }

            double baseRegenSpeed =
                entity.Api?.World?.Config
                    ?.GetString(
                        "playerHealthRegenSpeed",
                        "1")
                    ?.ToFloat() ?? 1;

            entity.WatchedAttributes.SetFloat(
                "regenSpeed",
                (float)(
                    baseRegenSpeed *
                    HealthRegenPenalty()));
            entity.WatchedAttributes.MarkPathDirty(
                "regenSpeed");

            float damageStatPenalty =
                DamageMultiplier() - 1f;

            entity.Stats?.Set(
                "walkspeed",
                "starvationmod",
                -MoveSpeedPenalty(),
                false);
            entity.Stats?.Set(
                "meleeWeaponsDamage",
                "starvationmod",
                damageStatPenalty,
                false);
            entity.Stats?.Set(
                "rangedWeaponsDamage",
                "starvationmod",
                damageStatPenalty,
                false);
            entity.Stats?.Set(
                "mechanicalsDamage",
                "starvationmod",
                damageStatPenalty,
                false);
            entity.Stats?.Set(
                "bowDrawingStrength",
                "starvationmod",
                damageStatPenalty,
                false);
            entity.Stats?.Set(
                "miningSpeedMul",
                "starvationmod",
                damageStatPenalty,
                false);

            if (energyReserves < DeathThreshold)
            {
                CommitPendingEnergy(true);

                entity.Die(
                    EnumDespawnReason.Death,
                    new DamageSource
                    {
                        Source = EnumDamageSource.Internal,
                        Type = EnumDamageType.Hunger
                    });
                return;
            }

            if (energyReserves < StarveThresholdExtreme)
            {
                entity.WatchedAttributes.SetFloat(
                    "intoxication",
                    Math.Max(
                        entity.WatchedAttributes.GetFloat(
                            "intoxication"),
                        1));
                entity.WatchedAttributes.MarkPathDirty(
                    "intoxication");
            }
        }

        private void ServerTick250(float deltaTime)
        {
            try
            {
                ServerTick250Core(deltaTime);
            }
            catch (Exception exception)
            {
                if (!fastTickFaultLogged)
                {
                    fastTickFaultLogged = true;
                    entity?.Api?.Logger.Error(
                        "[Starvation] Fast server tick disabled for " +
                        "this entity after an error: {0}",
                        exception);
                }
            }
        }

        private void ServerTick250Core(float deltaTime)
        {
            if (!TryGetReadyGameMode(out EnumGameMode gameMode))
            {
                return;
            }

            EnsureDailyAccounting();

            if (gameMode == EnumGameMode.Creative ||
                gameMode == EnumGameMode.Spectator)
            {
                return;
            }

            double safeWeight =
                Math.Max(1, bodyWeight);
            double safeMets =
                Math.Clamp(currentMETs, 0.5, 20);

            double kilojoulesPerGameDay =
                ModSystemStarvation.CalculateBMR(
                    safeWeight,
                    ageInYears,
                    heatIndexTemp) *
                safeMets;

            double gameSeconds =
                DeltaTimeToGameSeconds(deltaTime);

            if (gameSeconds <= 0)
            {
                return;
            }

            double kilojoulesBurned =
                kilojoulesPerGameDay /
                86400.0 *
                gameSeconds *
                GlobalConstants.HungerSpeedModifier;

            pendingEnergyDeltaKilojoules -=
                kilojoulesBurned;
            pendingCaloriesBurned +=
                kilojoulesBurned / 4.189;
            pendingMetGameSeconds +=
                safeMets * gameSeconds;
            pendingTrackedGameSeconds +=
                gameSeconds;

            CommitPendingEnergy(false);
        }

        public override void OnEntityReceiveSaturation(
            float saturation,
            EnumFoodCategory foodCat =
                EnumFoodCategory.Unknown,
            float saturationLossDelay = 10f,
            float nutritionGainMultiplier = 1f)
        {
            if (entity?.WatchedAttributes == null)
            {
                return;
            }

            if (TryGetReadyGameMode(out EnumGameMode gameMode) &&
                gameMode != EnumGameMode.Creative &&
                gameMode != EnumGameMode.Spectator &&
                saturation > 0)
            {
                EnsureDailyAccounting();
                UpdateDailyEstimates();

                double requestedCalories =
                    saturation *
                    ModSystemStarvation.Config
                        .SatietyToCaloriesFactor;

                double acceptedCalories =
                    requestedCalories;

                if (ModSystemStarvation.Config
                    .DailyIntakeLimitMultiplier > 0)
                {
                    double remainingCalories =
                        Math.Max(
                            0,
                            DailyCalorieLimit -
                            DailyCaloriesConsumed);

                    acceptedCalories =
                        Math.Min(
                            requestedCalories,
                            remainingCalories);
                }

                if (acceptedCalories > 0)
                {
                    DailyCaloriesConsumed +=
                        acceptedCalories;

                    pendingEnergyDeltaKilojoules +=
                        ModSystemStarvation
                            .CaloriesToKilojoules(
                                acceptedCalories);
                }
            }

            // Once the cap is reached, MaxSaturation makes ordinary foods
            // respect vanilla fullness. Consumables that explicitly ignore
            // fullness may still be used, but add no further energy.
            ResetHunger();
        }

        public override void OnEntityDeath(
            DamageSource damageSourceForDeath)
        {
            CommitPendingEnergy(true);

            base.OnEntityDeath(damageSourceForDeath);

            if (entity?.WatchedAttributes != null)
            {
                energyReserves = Math.Max(
                    StarveThresholdExtreme,
                    energyReserves);
            }
        }

        private void EnsureDailyAccounting()
        {
            if (entity?.World?.Calendar == null ||
                entity.WatchedAttributes == null)
            {
                return;
            }

            double currentDay =
                Math.Floor(
                    entity.World.Calendar.TotalDays);
            double storedDay =
                entity.WatchedAttributes.GetDouble(
                    AccountingDayAttribute,
                    double.NaN);

            if (double.IsNaN(storedDay))
            {
                SetSyncedDouble(
                    AccountingDayAttribute,
                    currentDay);
                ResetDailyCounters();

                // Store the default weight so it is synchronized immediately.
                bodyWeight = bodyWeight;

                lastEnergyCommitTotalHours =
                    entity.World.Calendar.TotalHours;

                UpdateDailyEstimates();
                return;
            }

            if (Math.Abs(currentDay - storedDay) < 0.5)
            {
                return;
            }

            // Close the previous day before resetting its counters.
            CommitPendingEnergy(true);

            SetSyncedDouble(
                PreviousDayCaloriesConsumedAttribute,
                DailyCaloriesConsumed);
            SetSyncedDouble(
                PreviousDayCaloriesBurnedAttribute,
                DailyCaloriesBurned);

            UpdateBodyWeightForNewDay();
            ResetDailyCounters();

            SetSyncedDouble(
                AccountingDayAttribute,
                currentDay);

            lastEnergyCommitTotalHours =
                entity.World.Calendar.TotalHours;

            UpdateDailyEstimates();
        }

        private void ResetDailyCounters()
        {
            DailyCaloriesConsumed = 0;
            DailyCaloriesBurned = 0;
            DailyMetGameSeconds = 0;
            DailyTrackedGameSeconds = 0;

            SetSyncedDouble(
                DailyAverageMetsAttribute,
                Math.Clamp(currentMETs, 0.95, 3));
            SetSyncedDouble(
                EstimatedDailyCaloriesAttribute,
                0);
            SetSyncedDouble(
                AllowanceDailyCaloriesAttribute,
                0);
            DailyCalorieLimit = 0;
        }

        private void CommitPendingEnergy(bool force)
        {
            if (entity?.World?.Calendar == null ||
                entity.WatchedAttributes == null)
            {
                return;
            }

            double currentTotalHours =
                entity.World.Calendar.TotalHours;

            if (double.IsNaN(lastEnergyCommitTotalHours))
            {
                lastEnergyCommitTotalHours =
                    currentTotalHours;
            }

            double commitIntervalHours =
                Math.Max(
                    0.1,
                    ModSystemStarvation.Config
                        .EnergyCommitIntervalHours);

            if (!force &&
                currentTotalHours -
                    lastEnergyCommitTotalHours <
                    commitIntervalHours)
            {
                return;
            }

            if (Math.Abs(pendingEnergyDeltaKilojoules) >
                0.0001)
            {
                energyReserves +=
                    pendingEnergyDeltaKilojoules;
            }

            if (pendingCaloriesBurned > 0)
            {
                DailyCaloriesBurned +=
                    pendingCaloriesBurned;
            }

            if (pendingMetGameSeconds > 0)
            {
                DailyMetGameSeconds +=
                    pendingMetGameSeconds;
            }

            if (pendingTrackedGameSeconds > 0)
            {
                DailyTrackedGameSeconds +=
                    pendingTrackedGameSeconds;
            }

            pendingEnergyDeltaKilojoules = 0;
            pendingCaloriesBurned = 0;
            pendingMetGameSeconds = 0;
            pendingTrackedGameSeconds = 0;
            lastEnergyCommitTotalHours =
                currentTotalHours;

            UpdateDailyEstimates();
        }

        private void UpdateDailyEstimates()
        {
            if (entity?.WatchedAttributes == null)
            {
                return;
            }

            double safeWeight =
                Math.Max(1, bodyWeight);
            double averageMets =
                GetAverageMetsForToday();

            // This is a live projection of actual expenditure and may change
            // as the player rests, walks or sprints.
            double estimatedDailyCalories =
                Math.Max(
                    0,
                    ModSystemStarvation.CalculateBMR(
                        safeWeight,
                        ageInYears,
                        heatIndexTemp,
                        true) *
                    averageMets *
                    GlobalConstants.HungerSpeedModifier);

            // The eating allowance deliberately ignores live activity.
            // It uses body weight, age, a neutral temperature and a fixed
            // configurable baseline activity level. Therefore sprinting can
            // increase actual burn without increasing today's food cap.
            double allowanceDailyCalories =
                Math.Max(
                    0,
                    ModSystemStarvation.CalculateBMR(
                        safeWeight,
                        ageInYears,
                        15,
                        true) *
                    ModSystemStarvation.Config
                        .AllowanceActivityMets *
                    GlobalConstants.HungerSpeedModifier);

            double intakeLimitMultiplier =
                ModSystemStarvation.Config
                    .DailyIntakeLimitMultiplier;

            double dailyLimit =
                intakeLimitMultiplier > 0
                    ? allowanceDailyCalories *
                        intakeLimitMultiplier
                    : double.MaxValue;

            SetSyncedDouble(
                DailyAverageMetsAttribute,
                averageMets);
            SetSyncedDouble(
                EstimatedDailyCaloriesAttribute,
                estimatedDailyCalories);
            SetSyncedDouble(
                AllowanceDailyCaloriesAttribute,
                allowanceDailyCalories);

            // Use zero on the client to represent an unlimited cap instead
            // of attempting to synchronize double.MaxValue.
            DailyCalorieLimit =
                intakeLimitMultiplier > 0
                    ? dailyLimit
                    : 0;
        }

        private double GetAverageMetsForToday()
        {
            double trackedSeconds =
                DailyTrackedGameSeconds +
                pendingTrackedGameSeconds;
            double metGameSeconds =
                DailyMetGameSeconds +
                pendingMetGameSeconds;

            if (trackedSeconds <= 0.001)
            {
                return Math.Clamp(
                    currentMETs,
                    0.95,
                    3);
            }

            return Math.Clamp(
                metGameSeconds / trackedSeconds,
                0.5,
                20);
        }

        private void UpdateBodyWeightForNewDay()
        {
            if (entity?.Properties == null)
            {
                return;
            }

            double oldWeight =
                Math.Max(
                    1,
                    bodyWeight);
            double eyeHeight =
                Math.Max(
                    0.1,
                    entity.Properties.EyeHeight);
            double targetWeight =
                ModSystemStarvation
                    .EnergyReservesToBMI(
                        energyReserves) *
                Math.Pow(eyeHeight, 2);

            double maximumDailyGain =
                oldWeight *
                ModSystemStarvation.Config
                    .MaxDailyWeightGainPercent /
                100.0;

            double newWeight =
                targetWeight > oldWeight
                    ? Math.Min(
                        targetWeight,
                        oldWeight + maximumDailyGain)
                    : targetWeight;

            bodyWeight = newWeight;

            SetSyncedDouble(
                LastDailyWeightChangeAttribute,
                newWeight - oldWeight);
        }

        private bool IsDailyIntakeLimitReached()
        {
            if (ModSystemStarvation.Config
                .DailyIntakeLimitMultiplier <= 0)
            {
                return false;
            }

            double limit =
                DailyCalorieLimit;

            return limit > 0 &&
                DailyCaloriesConsumed >=
                    limit - 0.01;
        }

        private bool TryGetReadyGameMode(
            out EnumGameMode gameMode)
        {
            gameMode = EnumGameMode.Survival;

            if (entity == null ||
                !entity.Alive ||
                entity.World?.Calendar == null ||
                entity.Properties == null ||
                entity.Pos == null ||
                entity.WatchedAttributes == null)
            {
                return false;
            }

            if (entity is EntityPlayer playerEntity)
            {
                IPlayer player =
                    playerEntity.Player;

                if (player?.WorldData == null)
                {
                    return false;
                }

                gameMode =
                    player.WorldData.CurrentGameMode;
            }

            return true;
        }

        private void SyncClientDisplayValues()
        {
            if (entity?.WatchedAttributes == null)
            {
                return;
            }

            SetSyncedDouble(
                "satietyToCaloriesFactor",
                ModSystemStarvation.Config
                    .SatietyToCaloriesFactor);
            SetSyncedDouble(
                "dailyIntakeLimitMultiplier",
                ModSystemStarvation.Config
                    .DailyIntakeLimitMultiplier);
            SetSyncedDouble(
                "allowanceActivityMets",
                ModSystemStarvation.Config
                    .AllowanceActivityMets);
            SetSyncedDouble(
                "energyCommitIntervalHours",
                ModSystemStarvation.Config
                    .EnergyCommitIntervalHours);
        }

        private void ResetHunger(
            bool forceAllowEating = false)
        {
            EntityBehaviorHunger hunger =
                entity?.GetBehavior<EntityBehaviorHunger>();

            if (hunger == null)
            {
                return;
            }

            if (!forceAllowEating &&
                IsDailyIntakeLimitReached())
            {
                hunger.Saturation =
                    hunger.MaxSaturation;
                return;
            }

            hunger.Saturation =
                Math.Min(
                    100,
                    hunger.MaxSaturation);
        }

        private void ResetStarvationEffects()
        {
            EntityBehaviorHealth health =
                entity?.GetBehavior<EntityBehaviorHealth>();

            if (health != null)
            {
                health.SetMaxHealthModifiers(
                    "starvationMod",
                    0f);
                health.SetMaxHealthModifiers(
                    "nutrientHealthMod",
                    0f);
            }

            if (entity?.Stats != null)
            {
                entity.Stats.Remove(
                    "walkspeed",
                    "starvationmod");
                entity.Stats.Remove(
                    "meleeWeaponsDamage",
                    "starvationmod");
                entity.Stats.Remove(
                    "rangedWeaponsDamage",
                    "starvationmod");
                entity.Stats.Remove(
                    "mechanicalsDamage",
                    "starvationmod");
                entity.Stats.Remove(
                    "bowDrawingStrength",
                    "starvationmod");
                entity.Stats.Remove(
                    "miningSpeedMul",
                    "starvationmod");
            }

            if (entity?.WatchedAttributes != null)
            {
                entity.WatchedAttributes.SetFloat(
                    "regenSpeed",
                    1f);
                entity.WatchedAttributes.MarkPathDirty(
                    "regenSpeed");
            }
        }

        private void SetSyncedDouble(
            string key,
            double value)
        {
            if (entity?.WatchedAttributes == null)
            {
                return;
            }

            double current =
                entity.WatchedAttributes.GetDouble(
                    key,
                    double.NaN);

            if (!double.IsNaN(current) &&
                Math.Abs(current - value) < 0.0001)
            {
                return;
            }

            entity.WatchedAttributes.SetDouble(
                key,
                value);
            entity.WatchedAttributes.MarkPathDirty(
                key);
        }

        public double MaxHealthPenalty()
        {
            return MaxHealthPenaltyForEnergy(
                energyReserves);
        }

        public static double MaxHealthPenaltyForEnergy(
            double energy)
        {
            return energy switch
            {
                > StarveThresholdMild => 0,
                <= StarveThresholdMild and
                    > StarveThresholdModerate => 2,
                <= StarveThresholdModerate and
                    > StarveThresholdSevere => 4,
                <= StarveThresholdSevere and
                    > StarveThresholdExtreme => 7,
                _ => 12
            };
        }

        public double HealthRegenPenalty()
        {
            return HealthRegenMultiplierForEnergy(
                energyReserves);
        }

        public static double HealthRegenMultiplierForEnergy(
            double energy)
        {
            return energy switch
            {
                > StarveThresholdMild => 1,
                <= StarveThresholdMild and
                    > StarveThresholdModerate => 1,
                <= StarveThresholdModerate and
                    > StarveThresholdSevere => 0.5,
                _ => 0
            };
        }

        public float DamageMultiplier()
        {
            return DamageMultiplierForEnergy(
                energyReserves);
        }

        public static float DamageMultiplierForEnergy(
            double energy)
        {
            return energy switch
            {
                > StarveThresholdMild => 1,
                <= StarveThresholdMild and
                    > StarveThresholdModerate => 1,
                <= StarveThresholdModerate and
                    > StarveThresholdSevere => 0.7f,
                <= StarveThresholdSevere and
                    > StarveThresholdExtreme => 0.5f,
                _ => 0.4f
            };
        }

        public float MoveSpeedPenalty()
        {
            float debuff =
                BaseMoveSpeedPenaltyForEnergy(
                    energyReserves);

            EntityBehaviorHunger hunger =
                entity?.GetBehavior<EntityBehaviorHunger>();

            if (hunger != null &&
                (hunger.SaturationLossDelayDairy > 0 ||
                 hunger.SaturationLossDelayFruit > 0 ||
                 hunger.SaturationLossDelayGrain > 0 ||
                 hunger.SaturationLossDelayProtein > 0 ||
                 hunger.SaturationLossDelayVegetable > 0))
            {
                debuff *= 0.5f;
            }

            return debuff;
        }

        public static float BaseMoveSpeedPenaltyForEnergy(
            double energy)
        {
            return energy switch
            {
                > StarveThresholdMild => 0,
                <= StarveThresholdMild and
                    > StarveThresholdModerate => 0,
                <= StarveThresholdModerate and
                    > StarveThresholdSevere => 0.2f,
                <= StarveThresholdSevere and
                    > StarveThresholdExtreme => 0.4f,
                _ => 0.6f
            };
        }

        public static HungerLevel EnergyToHungerLevel(
            double energy)
        {
            return energy switch
            {
                > 0 => HungerLevel.Satiated,
                > StarveThresholdMild =>
                    HungerLevel.Mild,
                <= StarveThresholdMild and
                    > StarveThresholdModerate =>
                    HungerLevel.Moderate,
                <= StarveThresholdModerate and
                    > StarveThresholdSevere =>
                    HungerLevel.Severe,
                <= StarveThresholdSevere and
                    > StarveThresholdExtreme =>
                    HungerLevel.VerySevere,
                _ => HungerLevel.Extreme
            };
        }

        private double DeltaTimeToGameSeconds(
            double deltaTime)
        {
            if (entity?.World?.Calendar == null)
            {
                return 0;
            }

            return deltaTime *
                entity.World.Calendar.SpeedOfTime *
                entity.World.Calendar.CalendarSpeedMul;
        }

        public override void OnEntityDespawn(
            EntityDespawnData despawn)
        {
            if (entity?.World?.Side ==
                EnumAppSide.Server)
            {
                CommitPendingEnergy(true);
            }

            if (serverListenerId != 0)
            {
                entity?.World?.UnregisterGameTickListener(
                    serverListenerId);
                serverListenerId = 0;
            }

            if (serverListenerSlowId != 0)
            {
                entity?.World?.UnregisterGameTickListener(
                    serverListenerSlowId);
                serverListenerSlowId = 0;
            }

            base.OnEntityDespawn(despawn);
        }

        public override string PropertyName()
        {
            return "starvation";
        }
    }
}

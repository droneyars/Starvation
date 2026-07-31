using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace Starvation
{
    public enum HungerLevel
    {
        Satiated,
        Mild,
        Moderate,
        Severe,
        VerySevere,
        Extreme
    };


    public sealed class StarvationConfig
    {
        /// <summary>
        /// Used for safe migration of older config files.
        /// </summary>
        public int ConfigVersion { get; set; } = 0;

        /// <summary>
        /// Number of food calories credited for each point of satiety.
        /// </summary>
        public double SatietyToCaloriesFactor { get; set; } = 2.5;

        /// <summary>
        /// Maximum credited food intake per in-game day, expressed as a
        /// multiple of the player's estimated daily energy requirement.
        /// Set to 0 to disable the cap.
        /// </summary>
        public double DailyIntakeLimitMultiplier { get; set; } = 5;

        /// <summary>
        /// Fixed activity level used only to calculate the daily food
        /// allowance. Live movement and sprinting do not change the cap.
        /// </summary>
        public double AllowanceActivityMets { get; set; } = 1.5;

        /// <summary>
        /// Maximum positive body-weight change per in-game day, expressed
        /// as a percentage of the player's weight at the start of the day.
        /// </summary>
        public double MaxDailyWeightGainPercent { get; set; } = 0.5;

        /// <summary>
        /// How often accumulated energy is committed to the synchronized
        /// reserve, measured in in-game hours.
        /// </summary>
        public double EnergyCommitIntervalHours { get; set; } = 1;
    }


    // "Controller" class that handles initialising the mod itself
    public class ModSystemStarvation  : ModSystem
    {
        public const double HEALTHY_BMI = 22;
        public const int PacketIdMets = 19877583;
        public const string ConfigFileName = "starvation.json";
        public const int CurrentConfigVersion = 5;

        private const string CharacterTabName = "Starvation";
        private const string CharacterSummaryLeftKey =
            "starvation-character-summary-left";
        private const string CharacterSummaryRightKey =
            "starvation-character-summary-right";
        private const long CharacterSheetRefreshMilliseconds =
            5000;

        public static StarvationConfig Config { get; private set; } =
            new StarvationConfig();

        Dictionary<HungerLevel, string> HungerLevelToText = new Dictionary<HungerLevel, string>
        { 
            // { HungerLevel.Satiated, Lang.Get("starvation:descr-satiated") },
            // { HungerLevel.Mild, Lang.Get("starvation:descr-starve-mild") },
            // { HungerLevel.Moderate, Lang.Get("starvation:descr-starve-moderate") },
            // { HungerLevel.Severe, Lang.Get("starvation:descr-starve-severe") },
            // { HungerLevel.VerySevere, Lang.Get("starvation:descr-starve-very-severe") },
            // { HungerLevel.Extreme, Lang.Get("starvation:descr-starve-extreme") },
        };

        public static ICoreClientAPI clientAPI;
        public static ICoreServerAPI serverAPI;

        GuiDialogCharacterBase characterDialog;
        bool characterTabAttached;
        long lastCharacterSheetRefreshMilliseconds;

       // Dictionary mapping animation names to METs
        // TODO add all "-fp" versions
        Dictionary<string, double> METsByActivity = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "walk", 4 },
            { "idle", 1.3 },
            { "helditemready", 1.5 },
            { "sitflooridle.", 1.3 },
            { "sitflooredge.", 1.3 },
            { "sprint", 12 },
            { "sprint-fp", 12 },
            { "sneakwalk", 2.5 },
            { "sneakidle", 1.3 },
            { "glide", 3.5 },
            { "swim", 5.3 },
            { "swimidle", 3.5 },
            { "jump", 8 },
            { "climbup", 8 },
            { "climbidle", 5 },
            { "sleep", 0.95 },
            { "coldidle", 4 },
            { "protecteyes", 1.5 },
            { "coldidleheld", 5 },
            { "holdunderarm", 1.5 },
            { "holdinglanternlefthand", 1.5 },
            { "holdbothhands", 1.5 },
            { "holdbothhandslarge", 2 },
            { "hurt", 1 },
            { "bowaim", 2.5 },
            { "bowaimcrude", 2.5 },
            { "bowaimlong", 2.5 },
            { "bowaimrecurve", 2.5 },
            { "bowhit", 1 },
            { "throwaim", 2 },
            { "throw", 4 },
            { "slingaimgreek", 2 },
            { "slingthrowgreek", 2 },
            { "slingaimbalearic", 2 },
            { "slingthrowbalearic", 2 },
            { "hit", 2 },
            { "smithing", 4 },
            { "smithingwide", 4 },
            { "knap", 3 },
            { "breaktool", 1.3 },
            { "breakhand", 1.3 },
            { "falx", 5 },
            { "swordhit", 2 },
            { "axechop", 5 },
            { "axeheld", 1.3 },
            { "axeready", 1.3 },
            { "hoe", 3.5 },
            { "water", 1.5 },
            { "shoveldig", 5 },
            { "shovelready", 1.3 },
            { "shovelidle", 1.3 },
            { "spearhit", 1.5 },
            { "spearready", 2.3 },
            { "spearidle", 2.3 },
            { "scythe", 2 },
            { "scytheIdle", 1.3 },
            { "scytheReady", 1.3 },
            { "hammerandchisel", 3 },
            { "shears", 4 },
            { "placeblock", 3 },
            { "interactstatic", 1.3 },
            { "twohandplaceblock", 4 },
            { "eat", 2 },
            { "wave", 1.5 },
            { "nod", 1.5 },
            { "bow", 1.5 },
            { "facepalm", 1.5 },
            { "cry", 1.5 },
            { "shrug", 1.5 },
            { "cheer", 1.5 },
            { "laugh", 1 },
            { "rage", 1.5 },
            { "panning", 2.8 },
            { "pour", 1.5 },
            { "petlarge", 1.5 },
            { "petsmall", 1.5 },
            { "crudeOarIdle", 2.3 },
            { "crudeOarStandingReady", 2.3 },
            { "crudeOarHit", 2 },
            { "crudeOarForward", 5.8 },
            { "crudeOarBackward", 5.8 },
            { "crudeOarReady", 2.3 },
            { "yawn", 2.3 },
            { "stretch", 2.3 },
            { "cough", 2.3 },
            { "headscratch", 1.5 },
            { "raiseshield-left", 2 },
            { "raiseshield-right", 2 },
            { "knifecut", 5 },
            { "knifestab", 5 },
            { "startfire", 3 },
            { "shieldBlock", 10 },
            { "chiselready", 1.5 },
            { "chiselhit", 3 },
            { "combatoverhaul-spear-idle", 2.3 },
            {"combatoverhaul-spear-ready", 2.3 },
            {"combatoverhaul-falx-slash", 1.95 }

        };


        public override void Start(ICoreAPI api)
        {
            // Called on server, before any content is actually loaded.
            base.Start(api);

            api.RegisterEntityBehaviorClass("starve", typeof(EntityBehaviorStarve));

        }


        // If you want to add or adjust attributes or properties of other game objects, do so in this method.
        public override void AssetsFinalize(ICoreAPI api)
        {
            base.AssetsFinalize(api);

            HungerLevelToText[HungerLevel.Satiated] = Lang.Get("starvation:descr-satiated");
            HungerLevelToText[HungerLevel.Mild] = Lang.Get("starvation:descr-starve-mild");
            HungerLevelToText[HungerLevel.Moderate] = Lang.Get("starvation:descr-starve-moderate");
            HungerLevelToText[HungerLevel.Severe] = Lang.Get("starvation:descr-starve-severe");
            HungerLevelToText[HungerLevel.VerySevere] = Lang.Get("starvation:descr-starve-very-severe");
            HungerLevelToText[HungerLevel.Extreme] = Lang.Get("starvation:descr-starve-extreme");

            // should not be needed as we reset satiety regularly in EntityBehaviorStarve.ResetHunger
            // GlobalConstants.HungerSpeedModifier = 0;

        }


        public override void StartServerSide(ICoreServerAPI sapi)
        {
            // Called on server, before any content is actually loaded.
            base.StartServerSide(sapi);

            serverAPI = sapi;

            try
            {
                Config =
                    sapi.LoadModConfig<StarvationConfig>(ConfigFileName) ??
                    new StarvationConfig();

                bool migratedLegacyFactor = false;

                // Only unversioned/v1 configs used 0.5 as the old default.
                if (Config.ConfigVersion < 2 &&
                    Math.Abs(
                        Config.SatietyToCaloriesFactor - 0.5) < 0.0001)
                {
                    Config.SatietyToCaloriesFactor = 2.5;
                    migratedLegacyFactor = true;
                }

                Config.ConfigVersion = CurrentConfigVersion;

                Config.SatietyToCaloriesFactor = Math.Clamp(
                    Config.SatietyToCaloriesFactor,
                    0,
                    10);
                Config.DailyIntakeLimitMultiplier = Math.Clamp(
                    Config.DailyIntakeLimitMultiplier,
                    0,
                    20);
                Config.AllowanceActivityMets = Math.Clamp(
                    Config.AllowanceActivityMets,
                    0.5,
                    5);
                Config.MaxDailyWeightGainPercent = Math.Clamp(
                    Config.MaxDailyWeightGainPercent,
                    0,
                    5);
                Config.EnergyCommitIntervalHours = Math.Clamp(
                    Config.EnergyCommitIntervalHours,
                    0.1,
                    24);

                sapi.StoreModConfig(Config, ConfigFileName);

                if (migratedLegacyFactor)
                {
                    sapi.Logger.Notification(
                        "[Starvation] Migrated the old 0.5 food " +
                        "conversion factor to 2.5 calories per satiety.");
                }

                sapi.Logger.Notification(
                    "[Starvation] Food conversion: {0:0.###} calories " +
                    "per satiety. Daily intake cap: {1:0.##}x a fixed " +
                    "{2:0.##}-MET allowance need. Maximum daily weight " +
                    "gain: {3:0.###}%. Energy reserve commit interval: " +
                    "{4:0.##} game hours.",
                    Config.SatietyToCaloriesFactor,
                    Config.DailyIntakeLimitMultiplier,
                    Config.AllowanceActivityMets,
                    Config.MaxDailyWeightGainPercent,
                    Config.EnergyCommitIntervalHours);
            }
            catch (Exception exception)
            {
                Config = new StarvationConfig
                {
                    ConfigVersion = CurrentConfigVersion
                };

                sapi.Logger.Error(
                    "[Starvation] Could not load {0}; using the default " +
                    "satiety conversion factor of {1}: {2}",
                    ConfigFileName,
                    Config.SatietyToCaloriesFactor,
                    exception.Message);

                try
                {
                    sapi.StoreModConfig(Config, ConfigFileName);
                }
                catch
                {
                    // The default remains usable even when the config cannot
                    // be written.
                }
            }
        }


        // Called from the client, when the game world is fully loaded and ready to start.
        public override void StartClientSide(ICoreClientAPI capi)
        {
            base.StartClientSide(capi);

            clientAPI = capi;

            clientAPI.Event.RegisterGameTickListener(
                ClientTick500,
                500);
        }


        private void TryAttachCharacterTab()
        {
            if (characterTabAttached ||
                clientAPI?.Gui?.LoadedGuis == null)
            {
                return;
            }

            foreach (object loadedGui in clientAPI.Gui.LoadedGuis)
            {
                if (loadedGui is not GuiDialogCharacterBase foundDialog)
                {
                    continue;
                }

                characterDialog = foundDialog;

                if (characterDialog.Tabs.Exists(
                    tab => tab.Name == CharacterTabName))
                {
                    characterTabAttached = true;
                    return;
                }

                int tabIndex =
                    characterDialog.RenderTabHandlers.Count;

                characterDialog.Tabs.Add(
                    new GuiTab
                    {
                        Name = CharacterTabName,
                        DataInt = tabIndex
                    });

                characterDialog.RenderTabHandlers.Add(
                    ComposeStarvationCharacterTab);

                characterTabAttached = true;

                clientAPI.Logger.Notification(
                    "[Starvation] Added Starvation tab to " +
                    "the character sheet.");

                return;
            }
        }


        private void ComposeStarvationCharacterTab(
            GuiComposer composer)
        {
            BuildCharacterSheetSummaries(
                out string leftText,
                out string rightText);

            CairoFont font =
                CairoFont.WhiteSmallText()
                    .WithFontSize(15f)
                    .WithLineHeightMultiplier(0.9);

            composer
                .AddDynamicText(
                    leftText,
                    font,
                    ElementBounds.Fixed(
                        0,
                        8,
                        190,
                        315),
                    CharacterSummaryLeftKey)
                .AddDynamicText(
                    rightText,
                    font,
                    ElementBounds.Fixed(
                        195,
                        8,
                        190,
                        315),
                    CharacterSummaryRightKey);
        }


        private void UpdateCharacterSheetTab()
        {
            if (characterDialog?.SingleComposer == null ||
                !characterDialog.IsOpened() ||
                clientAPI == null)
            {
                return;
            }

            long now = clientAPI.ElapsedMilliseconds;

            if (now - lastCharacterSheetRefreshMilliseconds <
                CharacterSheetRefreshMilliseconds)
            {
                return;
            }

            lastCharacterSheetRefreshMilliseconds = now;

            try
            {
                BuildCharacterSheetSummaries(
                    out string leftText,
                    out string rightText);

                GuiElementDynamicText leftSummary =
                    characterDialog.SingleComposer.GetDynamicText(
                        CharacterSummaryLeftKey);
                GuiElementDynamicText rightSummary =
                    characterDialog.SingleComposer.GetDynamicText(
                        CharacterSummaryRightKey);

                leftSummary?.SetNewTextAsync(leftText);
                rightSummary?.SetNewTextAsync(rightText);
            }
            catch
            {
                // These elements exist only while the Starvation tab
                // is the composed character tab.
            }
        }


        private void BuildCharacterSheetSummaries(
            out string leftText,
            out string rightText)
        {
            EntityPlayer player =
                clientAPI?.World?.Player?.Entity;

            if (player?.WatchedAttributes == null ||
                player.Properties == null)
            {
                leftText =
                    "Starvation data is not available yet.";
                rightText = string.Empty;
                return;
            }

            double energyKilojoules =
                player.WatchedAttributes.GetDouble(
                    "energyReserves",
                    0);
            double weight =
                player.WatchedAttributes.GetDouble(
                    "bodyWeight",
                    HealthyWeight(player));
            double height =
                Math.Max(
                    0.1,
                    player.Properties.EyeHeight);
            double bmi =
                weight / Math.Pow(height, 2);

            double factor =
                player.WatchedAttributes.GetDouble(
                    "satietyToCaloriesFactor",
                    Config.SatietyToCaloriesFactor);
            double capMultiplier =
                player.WatchedAttributes.GetDouble(
                    "dailyIntakeLimitMultiplier",
                    Config.DailyIntakeLimitMultiplier);
            double allowanceActivityMets =
                player.WatchedAttributes.GetDouble(
                    "allowanceActivityMets",
                    Config.AllowanceActivityMets);
            double commitIntervalHours =
                player.WatchedAttributes.GetDouble(
                    "energyCommitIntervalHours",
                    Config.EnergyCommitIntervalHours);

            double dailyConsumed =
                player.WatchedAttributes.GetDouble(
                    "dailyCaloriesConsumed",
                    0);
            double dailyBurned =
                player.WatchedAttributes.GetDouble(
                    "dailyCaloriesBurned",
                    0);
            double dailyLimit =
                player.WatchedAttributes.GetDouble(
                    "dailyCalorieLimit",
                    0);
            double estimatedDailyCalories =
                player.WatchedAttributes.GetDouble(
                    "estimatedDailyCalories",
                    0);
            double allowanceDailyCalories =
                player.WatchedAttributes.GetDouble(
                    "allowanceDailyCalories",
                    0);
            double averageMets =
                player.WatchedAttributes.GetDouble(
                    "dailyAverageMets",
                    1);
            double lastWeightChange =
                player.WatchedAttributes.GetDouble(
                    "lastDailyWeightChangeKg",
                    0);

            double dailySatietyConsumed =
                factor > 0
                    ? dailyConsumed / factor
                    : 0;
            double dailySatietyLimit =
                factor > 0 && dailyLimit > 0
                    ? dailyLimit / factor
                    : 0;
            double remainingCalories =
                dailyLimit > 0
                    ? Math.Max(
                        0,
                        dailyLimit - dailyConsumed)
                    : 0;

            HungerLevel hungerLevel =
                EntityBehaviorStarve.EnergyToHungerLevel(
                    energyKilojoules);

            string condition =
                HungerLevelToText.Get(
                    hungerLevel,
                    hungerLevel.ToString());

            double energyCalories =
                energyKilojoules / 4.189;

            double maxHealthPenalty =
                EntityBehaviorStarve
                    .MaxHealthPenaltyForEnergy(
                        energyKilojoules);
            double movePenaltyPercent =
                EntityBehaviorStarve
                    .BaseMoveSpeedPenaltyForEnergy(
                        energyKilojoules) * 100;
            double damagePenaltyPercent =
                (1 -
                    EntityBehaviorStarve
                        .DamageMultiplierForEnergy(
                            energyKilojoules)) * 100;
            double regenerationPercent =
                EntityBehaviorStarve
                    .HealthRegenMultiplierForEnergy(
                        energyKilojoules) * 100;

            string weightChangeText =
                lastWeightChange > 0
                    ? $"+{lastWeightChange:N2}"
                    : $"{lastWeightChange:N2}";

            StringBuilder left = new StringBuilder();

            left.AppendLine("STATUS");
            left.AppendLine($"State: {condition}");
            left.AppendLine(
                $"Energy: {Math.Round(energyCalories):N0} kcal");
            left.AppendLine(
                $"Reserve: {Math.Round(energyKilojoules):N0} kJ");
            left.AppendLine($"Weight: {weight:N2} kg");
            left.AppendLine($"BMI: {bmi:N1}");
            left.AppendLine($"Weight Δ: {weightChangeText} kg");
            left.AppendLine();
            left.AppendLine("TODAY");
            left.AppendLine(
                $"Food: {Math.Round(dailyConsumed):N0}/" +
                $"{Math.Round(dailyLimit):N0} kcal");
            left.AppendLine(
                $"Sat.: {Math.Round(dailySatietyConsumed):N0}/" +
                $"{Math.Round(dailySatietyLimit):N0}");
            left.AppendLine(
                $"Left: {Math.Round(remainingCalories):N0} kcal");
            left.Append(
                $"Burn: {Math.Round(dailyBurned):N0} kcal");

            StringBuilder right = new StringBuilder();

            right.AppendLine("ALLOWANCE");
            right.AppendLine(
                $"Basis: {Math.Round(allowanceDailyCalories):N0} kcal/d");
            right.AppendLine(
                $"Base MET: {allowanceActivityMets:N2}");
            right.AppendLine(
                $"Cap: {capMultiplier:N1}x");
            right.AppendLine(
                $"Actual: {Math.Round(estimatedDailyCalories):N0} kcal/d");
            right.AppendLine(
                $"Avg MET: {averageMets:N2}");
            right.AppendLine(
                $"kcal/sat: {factor:N2}");
            right.AppendLine(
                $"Update: {commitIntervalHours:N1} game h");
            right.AppendLine();
            right.AppendLine("EFFECTS");
            right.AppendLine(
                $"Health: -{maxHealthPenalty:N0}");
            right.AppendLine(
                $"Move: -{movePenaltyPercent:N0}%");
            right.AppendLine(
                $"Damage: -{damagePenaltyPercent:N0}%");
            right.Append(
                $"Regen: {regenerationPercent:N0}%");

            leftText = left.ToString();
            rightText = right.ToString();
        }



        // public static string GetLocalized(string key, string engDefault)
        // {
        //     if (Lang.HasTranslation(key))
        //     {
        //         return Lang.Get(key);
        //     } else {
        //         return engDefault;
        //     }
        // }


        // Called within the CLIENT, every 500 milliseconds.
        // The role of this function is to calculate the player's current expended METs.
        // This has to be done in the client because the server seems not to have access to 
        // the list of active animations.
        // Note:deltaTime is in SECONDS (i.e. 0.5)
        private void ClientTick500(float deltaTime)
        {
            TryAttachCharacterTab();

            EntityPlayer clientPlayer =
                clientAPI?.World?.Player?.Entity;
            if (clientPlayer == null ||
                !clientPlayer.Alive ||
                clientPlayer.WatchedAttributes == null)
            {
                return;
            }

            double mets = CalculateCurrentMETs(clientPlayer);

            clientPlayer.WatchedAttributes.SetDouble("currentMETs", mets);
            clientPlayer.WatchedAttributes.MarkPathDirty("currentMETs");

            clientAPI.Network.SendEntityPacket(
                clientPlayer.EntityId,
                PacketIdMets,
                SerializerUtil.Serialize(mets));

            UpdateCharacterSheetTab();
        }


        private static ClimateCondition GetClimateAtEntity(
            Entity entity,
            EnumGetClimateMode climateMode)
        {
            if (entity?.World?.BlockAccessor == null ||
                entity.World.Calendar == null ||
                entity.Pos == null)
            {
                return null;
            }

            try
            {
                BlockPos blockPos = new BlockPos(
                    (int)Math.Floor(entity.Pos.X),
                    (int)Math.Floor(entity.Pos.Y),
                    (int)Math.Floor(entity.Pos.Z),
                    entity.Pos.Dimension);

                return entity.World.BlockAccessor.GetClimateAt(
                    blockPos,
                    climateMode,
                    entity.World.Calendar.TotalDays);
            }
            catch
            {
                // Climate data may not yet be available while a player entity
                // is joining or changing dimensions. Use safe defaults.
                return null;
            }
        }


        public static double GetTemperatureAtEntity(Entity entity)
        {
            ClimateCondition climate = GetClimateAtEntity(
                entity,
                EnumGetClimateMode.ForSuppliedDate_TemperatureOnly);

            return climate?.Temperature ?? 15;
        }


        // Number from 0-1
        public static double GetRainfallAtEntity(Entity entity)
        {
            ClimateCondition climate = GetClimateAtEntity(
                entity,
                EnumGetClimateMode.ForSuppliedDateValues);

            return climate?.Rainfall ?? 0;
        }


        public static double GetHumidityAtEntity(Entity entity)
        {
            // Humidity correlates to rainfall pretty closely.
            return Math.Clamp(GetRainfallAtEntity(entity) * 100, 10, 90);
        }


        // Return a "healthy" weight for the (human) entity, in kg, using eyeHeight as its height. 
        public static double HealthyWeight(Entity entity)
        {
            double eyeHeight = Math.Max(0.1, entity?.Properties?.EyeHeight ?? 1.7);
            return HEALTHY_BMI * Math.Pow(eyeHeight, 2);
        }


        // Returns estimated heat index temperature in degrees celsius.
        //      ambientTemperature is the dry-bulb temperature in C
        //      relativeHumidity is a percentage 0-100
        // Equation from Steadman, R. G. (July 1979). "The Assessment of Sultriness" (!!) (via Wikipedia)
        public static double HeatIndexTemperature(double ambientTemperature, double relativeHumidity)
        {
            const double c1 = -8.78469476;
            const double c2 = 1.61139411;
            const double c3 = 2.33854884;
            const double c4 = -0.14611605;
            const double c5 = -0.012308094;
            const double c6 = -0.01642483;
            const double c7 = 0.002211732;
            const double c8 = 0.00072546;
            const double c9 = -0.000003582;
            return c1 + c2 * ambientTemperature 
                + c3 * relativeHumidity 
                + c4 * ambientTemperature * relativeHumidity 
                + c5 * Math.Pow(ambientTemperature, 2) 
                + c6 * Math.Pow(relativeHumidity, 2) 
                + c7 * Math.Pow(ambientTemperature, 2) * relativeHumidity 
                + c8 * ambientTemperature * Math.Pow(relativeHumidity, 2) 
                + c9 * Math.Pow(ambientTemperature, 2) * Math.Pow(relativeHumidity, 2) ;
        }


        // Returns (human) entity's basal metabolic rate in kilojoules/day
        // This is the "baseline" energy expended if engaged in no activity other than breathing.
        // Depends on age, body weight, sex (ignored), and heat index (basically = ambient temperature, but increased a bit in humid environments)
        // Accounts for increased BMR seen with adaptation to cold environment.
        // Does not account for shivering (occurs when core body temp unacceptably low)
        static public double CalculateBMR(double weightkg, double age, double tempC, bool calories = false)
        {
            // BMR in kcal = (13.6 * MASS) - (4.8 * AGE) + (147 * (1 if male, 0 if female)) - (4.3 * TEMP) + 857
            // kcal * 4.189 = kJ
            // temp is supposed to be high heat index temperature
            // assuming mass 64 kg, age 25, temp 15, BMR = approx 6800 kJ
            // double temp = entity.World.BlockAccessor.GetClimateAt(entity.Pos.AsBlockPos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, entity.World.Calendar.TotalDays).Temperature;
            // double humidity = 50;
            return (13.6 * weightkg - (4.8 * age) + 73.5 - (4.3 * tempC) + 857) * (calories? 1 : 4.189);
        }


        static public double CaloriesToKilojoules(double cal)
        {
            return cal * 4.189;
        }


        static public double EnergyReservesToBMI(double energy)
        {
            return 5.5948e-12 * Math.Pow(energy, 2) + 0.0000226104 * energy + 21.9336;
        }


        // Return the METs of the client player entity's current activity, as judged by active animations.
        // We do this client side because the server version of the player entity doesn't have any of the active animations
        // that we're interested in
        public double CalculateCurrentMETs(EntityPlayer entity)
        {
            //EntityPlayer clientPlayer = capi.World.Player.Entity;
            double maxMETs = 1;
            // list of all active animations

            if (entity?.AnimManager?.ActiveAnimationsByAnimCode == null)
            {
                return maxMETs;
            }

            List<string> keyList = new List<string>(entity.AnimManager.ActiveAnimationsByAnimCode.Keys);

            foreach (string aName in keyList)
            {
                // Potential Optimisation:
                // Making second Dict with -fp may make it a bit faster, but when it occurs every 500ms I don't think it's a big deal
                string animName = aName.Replace("-fp", "");

                if (METsByActivity.TryGetValue(animName, out double value))
                {
                    maxMETs = Math.Max(maxMETs, value);
                }
            }
            return maxMETs;
        }

    }
}

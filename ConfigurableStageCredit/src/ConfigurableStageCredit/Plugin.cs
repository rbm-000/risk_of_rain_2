using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace ConfigurableStageCredit
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.configurablestagecredit.plugin";
        public const string PluginName = "ConfigurableStageCredit";
        public const string PluginVersion = "0.3.4";

        internal static ManualLogSource ModLog;

        // monsterCredit is private in some game builds, so use reflection for compatibility.
        private static readonly FieldInfo MonsterCreditField = typeof(SceneDirector).GetField(
            "monsterCredit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private CreditSettings _enemyCredits;
        private CreditSettings _lootCredits;
        private ConfigEntry<bool> _printCreditOnStageStart;
        // A stage number is not a unique map identifier: the Bazaar and a stage can
        // share the same stage count. Track each SceneDirector separately instead.
        private readonly Dictionary<int, CreditPrintSnapshot> _pendingCreditPrints = new Dictionary<int, CreditPrintSnapshot>();
        private readonly HashSet<int> _reportedCreditPrints = new HashSet<int>();

        private void Awake()
        {
            ModLog = Logger;
            _enemyCredits = new CreditSettings(Config, "1-Enemy Credit");
            _lootCredits = new CreditSettings(Config, "2-Loot Credit");
            _printCreditOnStageStart = Config.Bind(
                "0-General",
                "PrintCreditOnStageStart",
                false,
                "When enabled, print the enemy and loot credit multipliers, original budgets, and modified budgets for every populated map.");

            SceneDirector.onPrePopulateMonstersSceneServer += OnPrePopulateMonsters;
            SceneDirector.onPrePopulateSceneServer += OnPrePopulateInteractables;

            if (MonsterCreditField == null)
            {
                Logger.LogError("Could not find SceneDirector.monsterCredit. Enemy credit scaling will be disabled; loot credit scaling can still work.");
            }

            Logger.LogInfo("Loaded. Enemy and loot credit multipliers are configured independently in the BepInEx config file.");
        }

        private void OnDestroy()
        {
            SceneDirector.onPrePopulateMonstersSceneServer -= OnPrePopulateMonsters;
            SceneDirector.onPrePopulateSceneServer -= OnPrePopulateInteractables;
        }

        private static int GetCurrentStageNumber()
        {
            Run run = Run.instance;
            // stageClearCount is 0 on the first stage, so the user-facing stage number is +1.
            return run == null ? 1 : Math.Max(1, run.stageClearCount + 1);
        }

        private void OnPrePopulateMonsters(SceneDirector director)
        {
            if (!NetworkServer.active || director == null || MonsterCreditField == null)
                return;

            ScaleReflectedCredit(director, MonsterCreditField, _enemyCredits, "enemy");
        }

        private void OnPrePopulateInteractables(SceneDirector director)
        {
            if (!NetworkServer.active || director == null)
                return;

            int stage = GetCurrentStageNumber();
            float multiplier = _lootCredits.GetMultiplier(stage);
            int before = director.interactableCredit;
            int after = ScaleIntegerCredit(before, multiplier);
            director.interactableCredit = after;

            ModLog.LogDebug(string.Format(
                CultureInfo.InvariantCulture,
                "Stage {0}: loot credit {1} -> {2} (x{3:0.###}).",
                stage, before, after, multiplier));

            RecordCreditForChat(director, stage, false, before, after, multiplier);
        }

        private void ScaleReflectedCredit(SceneDirector director, FieldInfo field, CreditSettings settings, string label)
        {
            try
            {
                object rawCredit = field.GetValue(director);
                if (rawCredit == null)
                    return;

                int stage = GetCurrentStageNumber();
                double before = Convert.ToDouble(rawCredit, CultureInfo.InvariantCulture);
                float multiplier = settings.GetMultiplier(stage);
                double scaled = Math.Max(0d, before * multiplier);
                object converted = ConvertCreditToFieldType(scaled, field.FieldType);
                field.SetValue(director, converted);
                double after = Convert.ToDouble(converted, CultureInfo.InvariantCulture);

                ModLog.LogDebug(string.Format(
                    CultureInfo.InvariantCulture,
                    "Stage {0}: {1} credit {2:0.##} -> {3:0.##} (x{4:0.###}).",
                    stage, label, before, after, multiplier));

                RecordCreditForChat(director, stage, true, before, after, multiplier);
            }
            catch (Exception ex)
            {
                ModLog.LogError("Failed to scale enemy spawn credit: " + ex);
            }
        }

        private void RecordCreditForChat(SceneDirector director, int stage, bool isEnemy, double vanillaCredit, double newCredit, float multiplier)
        {
            if (_printCreditOnStageStart == null || !_printCreditOnStageStart.Value || director == null)
                return;

            int sceneHandle = director.gameObject.scene.handle;
            if (_reportedCreditPrints.Contains(sceneHandle))
                return;

            if (!_pendingCreditPrints.TryGetValue(sceneHandle, out CreditPrintSnapshot snapshot))
            {
                snapshot = new CreditPrintSnapshot(stage);
                _pendingCreditPrints.Add(sceneHandle, snapshot);
            }

            if (isEnemy)
            {
                snapshot.EnemyVanillaCredit = vanillaCredit;
                snapshot.EnemyNewCredit = newCredit;
                snapshot.EnemyMultiplier = multiplier;
                snapshot.HasEnemyCredit = true;
            }
            else
            {
                snapshot.LootVanillaCredit = vanillaCredit;
                snapshot.LootNewCredit = newCredit;
                snapshot.LootMultiplier = multiplier;
                snapshot.HasLootCredit = true;
            }

            // Do not publish an incomplete report. The callbacks can run in either order.
            if (!snapshot.HasEnemyCredit || !snapshot.HasLootCredit)
                return;

            _reportedCreditPrints.Add(sceneHandle);
            _pendingCreditPrints.Remove(sceneHandle);

            string message = string.Format(
                CultureInfo.InvariantCulture,
                "<style=cEvent>[ConfigurableStageCredit]</style> Stage {0}\n   Enemy x{1:0.###}: vanilla {2:0.##}, new {3:0.##}\n   Loot x{4:0.###}: vanilla {5:0.##}, new {6:0.##}",
                snapshot.StageNumber,
                snapshot.EnemyMultiplier,
                snapshot.EnemyVanillaCredit,
                snapshot.EnemyNewCredit,
                snapshot.LootMultiplier,
                snapshot.LootVanillaCredit,
                snapshot.LootNewCredit);

            StartCoroutine(BroadcastCreditReportAfterSceneLoad(director, message));
        }

        private IEnumerator BroadcastCreditReportAfterSceneLoad(SceneDirector director, string message)
        {
            // The pre-population callbacks run during scene setup. Delay the actual
            // broadcast slightly so the new scene's HUD/chat exists when it arrives.
            yield return new WaitForSecondsRealtime(1.0f);

            if (!NetworkServer.active || director == null)
                yield break;

            // If another map finished loading during the delay, don't announce stale
            // credit data from the previous scene after the player has already left it.
            if (director.gameObject.scene.handle != SceneManager.GetActiveScene().handle)
                yield break;

            bool sent = false;
            try
            {
                Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = message });
                sent = true;
            }
            catch (Exception ex)
            {
                ModLog.LogWarning("Could not print the scene credit report to chat: " + ex.Message);
            }

            if (!sent)
                yield break;

        }

        private sealed class CreditPrintSnapshot
        {
            internal readonly int StageNumber;
            internal bool HasEnemyCredit;
            internal bool HasLootCredit;
            internal double EnemyVanillaCredit;
            internal double EnemyNewCredit;
            internal float EnemyMultiplier;
            internal double LootVanillaCredit;
            internal double LootNewCredit;
            internal float LootMultiplier;

            internal CreditPrintSnapshot(int stageNumber)
            {
                StageNumber = stageNumber;
            }
        }

        private static object ConvertCreditToFieldType(double value, Type fieldType)
        {
            value = Math.Max(0d, value);

            if (fieldType == typeof(float))
                return (float)Math.Min(value, float.MaxValue);
            if (fieldType == typeof(double))
                return value;
            if (fieldType == typeof(int))
                return (int)Math.Min(int.MaxValue, Math.Round(value, MidpointRounding.AwayFromZero));
            if (fieldType == typeof(uint))
                return (uint)Math.Min(uint.MaxValue, Math.Round(value, MidpointRounding.AwayFromZero));
            if (fieldType == typeof(long))
            {
                if (value >= (double)long.MaxValue)
                    return long.MaxValue;
                return (long)Math.Round(value, MidpointRounding.AwayFromZero);
            }
            if (fieldType == typeof(ulong))
            {
                if (value >= (double)ulong.MaxValue)
                    return ulong.MaxValue;
                return (ulong)Math.Round(value, MidpointRounding.AwayFromZero);
            }

            return Convert.ChangeType(value, fieldType, CultureInfo.InvariantCulture);
        }

        private static int ScaleIntegerCredit(int credit, float multiplier)
        {
            double scaled = Math.Max(0d, (double)credit * multiplier);
            return (int)Math.Min(int.MaxValue, Math.Round(scaled, MidpointRounding.AwayFromZero));
        }
    }

    internal sealed class CreditSettings
    {
        private readonly ConfigFile _config;
        private readonly ConfigEntry<float> _startingMultiplier;
        private readonly ConfigEntry<float> _stageIncrement;
        private readonly ConfigEntry<float> _loopIncrement;
        private readonly ConfigEntry<float> _minimumMultiplier;
        private readonly ConfigEntry<float> _maximumMultiplier;
        private bool _normalizing;

        internal CreditSettings(ConfigFile config, string section)
        {
            _config = config;
            _startingMultiplier = config.Bind(section, "StartingMultiplier", 1.0f,
                "Multiplier for stage 1. 1.15 means 15% more credits than the vanilla budget. If below MinimumMultiplier, it is set to MinimumMultiplier.");
            _stageIncrement = config.Bind(section, "StageIncrement", 0.0f,
                "Additive change to the multiplier between stages, except when entering a new 5-stage loop.");
            _loopIncrement = config.Bind(section, "LoopIncrement", 0.0f,
                "Additive change to the multiplier when entering a new loop (stages 6, 11, 16, etc.), replacing StageIncrement for that transition.");
            _minimumMultiplier = config.Bind(section, "MinimumMultiplier", 0.0f,
                "Lower bound for the calculated multiplier. Negative values are reset to 0.");
            _maximumMultiplier = config.Bind(section, "MaximumMultiplier", 10.0f,
                "Upper bound for the calculated multiplier. If below MinimumMultiplier, it is set to MinimumMultiplier.");

            _startingMultiplier.SettingChanged += OnSettingChanged;
            _stageIncrement.SettingChanged += OnSettingChanged;
            _loopIncrement.SettingChanged += OnSettingChanged;
            _minimumMultiplier.SettingChanged += OnSettingChanged;
            _maximumMultiplier.SettingChanged += OnSettingChanged;

            NormalizeSettings();
        }

        internal float GetMultiplier(int stageNumber)
        {
            NormalizeSettings();

            double minimum = _minimumMultiplier.Value;
            double maximum = _maximumMultiplier.Value;
            double multiplier = ClampMultiplier(_startingMultiplier.Value, minimum, maximum);
            int lastStage = Math.Max(1, stageNumber);

            // Apply each transition to the current (already clamped) multiplier.
            // The transitions into stages 6, 11, 16, etc. use LoopIncrement instead
            // of StageIncrement; every other transition uses StageIncrement.
            for (int nextStage = 2; nextStage <= lastStage; nextStage++)
            {
                bool enteringNewLoop = (nextStage - 1) % 5 == 0;
                multiplier += enteringNewLoop ? _loopIncrement.Value : _stageIncrement.Value;
                multiplier = ClampMultiplier(multiplier, minimum, maximum);
            }

            return (float)multiplier;
        }

        private static double ClampMultiplier(double value, double minimum, double maximum)
        {
            if (double.IsNaN(value))
                value = minimum;
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private void OnSettingChanged(object sender, EventArgs eventArgs)
        {
            NormalizeSettings();
        }

        private void NormalizeSettings()
        {
            if (_normalizing)
                return;

            bool changed = false;
            _normalizing = true;
            try
            {
                if (IsInvalidNumber(_startingMultiplier.Value))
                {
                    _startingMultiplier.Value = 1.0f;
                    changed = true;
                }
                if (IsInvalidNumber(_stageIncrement.Value))
                {
                    _stageIncrement.Value = 0.0f;
                    changed = true;
                }
                if (IsInvalidNumber(_loopIncrement.Value))
                {
                    _loopIncrement.Value = 0.0f;
                    changed = true;
                }

                float minimum = _minimumMultiplier.Value;
                if (IsInvalidNumber(minimum) || minimum < 0.0f)
                {
                    minimum = 0.0f;
                    _minimumMultiplier.Value = minimum;
                    changed = true;
                }

                float maximum = _maximumMultiplier.Value;
                if (IsInvalidNumber(maximum) || maximum < minimum)
                {
                    maximum = minimum;
                    _maximumMultiplier.Value = maximum;
                    changed = true;
                }
            }
            finally
            {
                _normalizing = false;
            }

            // Persist corrections so an invalid min/max pair is fixed in the config file too.
            if (changed)
            {
                try
                {
                    _config.Save();
                }
                catch (Exception ex)
                {
                    Plugin.ModLog.LogWarning("Could not save normalized settings: " + ex.Message);
                }
            }
        }

        private static bool IsInvalidNumber(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value);
        }
    }
}

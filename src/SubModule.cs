﻿using Bannerlord.ButterLib.Common.Extensions;

using HarmonyLib;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Events;

using System;
using System.ComponentModel;
using System.Linq;

using StoryMode.Behaviors;
using StoryMode.CharacterCreationSystem;
using StoryMode.StoryModeObjects;
using StoryMode.StoryModePhases;

using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace QuickStart
{
    public sealed class SubModule : MBSubModuleBase
    {
        public static string Version => "1.0.1";

        public static string Name => typeof(SubModule).Namespace;

        public static string DisplayName => Name;

        public static string HarmonyDomain => "com.zijistark.bannerlord." + Name.ToLower();

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Instance = this;
            this.AddSerilogLoggerProvider($"{Name}.log", new[] { $"{Name}.*" }, config => config.MinimumLevel.Is(LogEventLevel.Verbose));
        }

        protected override void OnSubModuleUnloaded() => Log.LogInformation($"Unloaded {Name}!");

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();

            if (!_hasLoaded)
            {
                _hasLoaded = true;

                Log = this.GetServiceProvider().GetRequiredService<ILogger<SubModule>>();
                Log.LogInformation($"Loading {Name}...");

                if (!Patches.CharacterCreationStatePatch.Apply(new Harmony(HarmonyDomain)))
                    throw new Exception($"{nameof(Patches.CharacterCreationStatePatch)} failed to apply!");

                if (McmSettings.Instance is { } settings)
                {
                    Log.LogDebug("MCM settings instance found.");

                    // Copy current settings to master config
                    Config.CopyFrom(settings);

                    // Register for settings property-changed events
                    settings.PropertyChanged += McmSettings_OnPropertyChanged;
                }
                else
                    Log.LogDebug("MCM settings instance NOT found. Using defaults.");

                Log.LogInformation($"Configuration:\n{Config.ToDebugString()}");

                if (Config.DisableIntroVideo)
                {
                    Log.LogTrace("Disabling intro video...");
                    AccessTools.DeclaredField(typeof(Module), "_splashScreenPlayed")?.SetValue(Module.CurrentModule, true);
                }

                Log.LogInformation($"Loaded {Name}!");
                InformationManager.DisplayMessage(new InformationMessage($"Loaded {DisplayName}", SignatureTextColor));
            }
        }

        private static void McmSettings_OnPropertyChanged(object sender, PropertyChangedEventArgs args)
        {
            if (sender is McmSettings settings && args.PropertyName == McmSettings.SaveTriggered)
            {
                Config.CopyFrom(settings);
                Log.LogInformation($"MCM triggered save of settings. New configuration:\n{Config.ToDebugString()}");
            }
        }

        internal void OnCultureStage(CharacterCreationState state)
        {
            DisableElderBrother();
            TrainingFieldCampaignBehavior.SkipTutorialMission = true;

            if (!Config.ShowCultureStage)
                SkipCultureStage(state);
            else
                Log.LogTrace("Culture selection stage is now under manual control.");
        }

        internal void OnFaceGenStage(CharacterCreationState state)
        {
            if (!Config.ShowFaceGenStage)
                SkipFaceGenStage(state);
            else
                Log.LogTrace("Face generator stage is now under manual control.");
        }

        internal void OnGenericStage(CharacterCreationState state)
        {
            if (!Config.ShowGenericStage)
                SkipGenericStage(state);
            else
                Log.LogTrace("Generic stage is now under manual control.");
        }

        internal void OnReviewStage(CharacterCreationState state) => SkipFinalStages(state);

        internal void OnOptionsStage(CharacterCreationState state) => _ = state;

        private void SkipCultureStage(CharacterCreationState state)
        {
            Log.LogTrace("Skipping culture selection stage...");

            if (CharacterCreationContent.Instance.Culture is null)
            {
                var culture = CharacterCreationContent.Instance.GetCultures().GetRandomElement();
                Log.LogDebug($"Randomly-selected player culture: {culture.Name}");

                CharacterCreationContent.Instance.Culture = culture;
                CharacterCreationContent.CultureOnCondition(state.CharacterCreation);
                state.NextStage();
            }
        }

        private void SkipFaceGenStage(CharacterCreationState state)
        {
            Log.LogTrace("Skipping face generator stage...");
            state.NextStage();
            // MAYBE-TODO: Skipping this quickly seems to always result in the same bald man (haven't tried a woman),
            //             which is not the actual default for slider settings. It'd be nice to be able to use a
            //             potentially configured BodyProperties key.
        }

        private void SkipGenericStage(CharacterCreationState state)
        {
            Log.LogTrace("Skipping generic stage...");
            var charCreation = state.CharacterCreation;

            for (int i = 0; i < charCreation.CharacterCreationMenuCount; ++i)
            {
                var option = charCreation.GetCurrentMenuOptions(i).Where(o => o.OnCondition is null || o.OnCondition()).GetRandomElement();

                if (option is not null)
                    charCreation.RunConsequence(option, i, false);
            }

            state.NextStage();
        }

        private void SkipFinalStages(CharacterCreationState state)
        {
            ChangeClanName(null);

            if (state.CurrentStage is CharacterCreationReviewStage)
            {
                Log.LogTrace("Skipping review stage...");
                state.NextStage();
            }

            if (state.CurrentStage is CharacterCreationOptionsStage)
            {
                Log.LogTrace("Skipping campaign options stage...");
                state.NextStage();
            }

            Log.LogTrace("Skipping tutorial phase...");

            if (Campaign.Current.GetCampaignBehavior<TrainingFieldCampaignBehavior>() is { } behavior)
            {
                AccessTools.Field(typeof(TrainingFieldCampaignBehavior), "_talkedWithBrotherForTheFirstTime").SetValue(behavior, true);
                TutorialPhase.Instance.PlayerTalkedWithBrotherForTheFirstTime();
            }

            DisableElderBrother(isFirst: false); // Do it again at the end for good measure
            StoryMode.StoryMode.Current.MainStoryLine.CompleteTutorialPhase(isSkipped: true);

            if (GameStateManager.Current.ActiveState is not MapState)
            {
                Log.LogCritical("Completed tutorial phase, but this did not result in a MapState! Aborting.");
                return;
            }

            TeleportPlayerToSettlement();

            if (Config.PromptForPlayerName)
                PromptForPlayerName();
            else if (Config.PromptForClanName) // Exclusive here but not upon dismissal of the player name inquiry
                PromptForClanName();

            if (Config.OpenBannerEditor)
                OpenBannerEditor();
        }

        private static void DisableElderBrother(bool isFirst = true)
        {
            var brother = (Hero)AccessTools.Property(typeof(StoryModeHeroes), "ElderBrother").GetValue(null);

            if (isFirst)
                PartyBase.MainParty.MemberRoster.RemoveTroop(brother.CharacterObject, 1);

            brother.ChangeState(Hero.CharacterStates.Disabled);
            brother.Clan = CampaignData.NeutralFaction;
        }

        private static void TeleportPlayerToSettlement()
        {
            foreach (var stringId in StartSettlementsToTry)
            {
                var settlement = Settlement.Find(stringId);

                if (settlement is not null)
                {
                    TeleportPlayerToSettlementInternal(settlement);
                    return;
                }
            }

            Log.LogDebug("Couldn't find one of predefined settlements for player teleportation. Choosing first available town...");
            var backupSettlement = Settlement.All.FirstOrDefault(s => s.IsTown);

            if (backupSettlement is null)
                Log.LogInformation("No suitable town found on map. Skipping player teleportation.");
            else
                TeleportPlayerToSettlementInternal(backupSettlement);
        }

        private static void TeleportPlayerToSettlementInternal(Settlement settlement)
        {
            MobileParty.MainParty.Position2D = settlement.GatePosition;
            ((MapState)GameStateManager.Current.ActiveState).Handler.TeleportCameraToMainParty();
            Log.LogTrace($"Teleported player directly to the gates of {settlement.Name}");
        }

        private static void PromptForPlayerName()
        {
            InformationManager.ShowTextInquiry(new TextInquiryData(new TextObject("Select your player name: ").ToString(),
                                                                   string.Empty,
                                                                   true,
                                                                   false,
                                                                   GameTexts.FindText("str_done", null).ToString(),
                                                                   null,
                                                                   new Action<string>(ChangePlayerName),
                                                                   null,
                                                                   false,
                                                                   new Func<string, bool>(IsPlayerNameApplicable)), false);
        }

        private static bool IsPlayerNameApplicable(string txt) => txt.Length <= 24 && txt.Length > 0;

        private static void ChangePlayerName(string? name)
        {
            var txtName = new TextObject(name ?? DefaultPlayerName);
            Hero.MainHero.Name = Hero.MainHero.FirstName = txtName;
            Log.LogTrace($"Set player name: {Hero.MainHero.Name}");
            InformationManager.DisplayMessage(new InformationMessage($"Set player name to: {Hero.MainHero.Name}", SignatureTextColor));

            if (Config.PromptForClanName)
                PromptForClanName();
        }

        private static void PromptForClanName()
        {
            InformationManager.ShowTextInquiry(new TextInquiryData(new TextObject("{=JJiKk4ow}Select your family name: ").ToString(),
                                                                   string.Empty,
                                                                   true,
                                                                   false,
                                                                   GameTexts.FindText("str_done", null).ToString(),
                                                                   null,
                                                                   new Action<string>(ChangeClanName),
                                                                   null,
                                                                   false,
                                                                   new Func<string, bool>(IsClanNameApplicable)), false);
        }

        private static bool IsClanNameApplicable(string txt) => txt.Length <= 50 && txt.Length > 0;

        private static void ChangeClanName(string? name)
        {
            var txtName = new TextObject(name ?? DefaultPlayerClanName);
            Clan.PlayerClan.InitializeClan(txtName, txtName, Clan.PlayerClan.Culture, Clan.PlayerClan.Banner);
            Log.LogTrace($"Set player clan name: {Clan.PlayerClan.Name}");
            InformationManager.DisplayMessage(new InformationMessage($"Set player clan name to: {Clan.PlayerClan.Name}", SignatureTextColor));
        }

        private static void OpenBannerEditor()
            => Game.Current.GameStateManager.PushState(Game.Current.GameStateManager.CreateState<BannerEditorState>(), 0);

        /* Non-Public Data */

        private static readonly Color SignatureTextColor = Color.FromUint(0x00F16D26);

        private const string DefaultPlayerClanName = "Playerclan";
        private const string DefaultPlayerName = "Player";

        private static readonly string[] StartSettlementsToTry = new[]
        {
            "town_EN1", // Epicrotea
            "town_B1", // Marunath
            "town_EW2", // Zeonica
        };

        private static ILogger Log { get; set; } = default!;

        internal static SubModule Instance { get; private set; } = default!;

        private bool _hasLoaded;
    }
}
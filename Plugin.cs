using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using RhythmRift.Enemies;
using RhythmRift;
using Unity.Mathematics;
using System;
using Shared.RhythmEngine;
using System.Reflection;
using Shared.PlayerData;
using Shared.SceneLoading.Payloads;
using Newtonsoft.Json;
using Shared.TrackSelection;
using Shared.SceneLoading;
using Shared;
using Shared.Analytics;
using Shared.DLC;

namespace HitMapper;

[BepInPlugin("main.rotn.plugins.hitmapper", "Hitmapper", "1.0.0.0")]
public class HitMapperPlugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;

    public static object get_field_by_name( object instance, string name ){
        FieldInfo property = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        return property.GetValue(instance);
    }

    public static object get_prop_by_name( object instance, string name ){
        PropertyInfo property = instance.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
        return property.GetValue(instance);
    }

    private void Awake()
    {
        // Plugin startup logic
        Logger = base.Logger;

        Harmony.CreateAndPatchAll(typeof(HitMapperPlugin));

        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

        Shared.BugSplatAccessor.Instance.BugSplat.ShouldPostException = ex => false;
    }

    public static Beatmap active_beatmap;

    public static List<object> hit_events = [];

    public static float update_beat = 0;
    public static float update_time = 0;

    [HarmonyPatch( typeof(Beatmap), "LoadFromJsonString")]
    [HarmonyPostfix]
    public static Beatmap BeatmapLoad( Beatmap __result ){
        hit_events = [];
        active_beatmap = __result;
        return __result;
    }

    public static Dictionary<string, string> BasicEnemyEvent( RREnemy __instance, bool is_hit ){
        Dictionary<string, string> hit_data = new Dictionary<string, string>();
        
        hit_data["GUID"] = __instance.GroupId.ToString();
        hit_data["ID"] = __instance.EnemyTypeId.ToString();
      
        hit_data["Facing"] = __instance.IsFacingLeft ? "Left" : "Right";
        hit_data["Health"] = __instance.CurrentHealthValue.ToString();

        if( is_hit ){
            int2 hit_pos =__instance.CurrentGridPosition.y == 0 ? __instance.CurrentGridPosition : __instance.TargetGridPosition;
            hit_data["X"] = hit_pos.x.ToString();
            hit_data["Y"] = hit_pos.y.ToString();

            hit_data["Beat"] = __instance.TargetHitBeatNumber.ToString();
            hit_data["Time"] = active_beatmap.GetTimeFromBeatNumber( __instance.TargetHitBeatNumber ).ToString();
        } else {
            int2 hit_pos = __instance.CurrentGridPosition;
            hit_data["X"] = hit_pos.x.ToString();
            hit_data["Y"] = hit_pos.y.ToString();

            hit_data["Beat"] = update_beat.ToString();
            hit_data["Time"] = update_time.ToString();
        }
        hit_data["Spawn"] = __instance.SpawnTrueBeatNumber.ToString();
        hit_data["Vibe"] = __instance.IsPartOfVibeChain.ToString(); 

        hit_data["Burning"] = __instance.HasStatusEffectActive( RREnemyStatusEffect.Burning ).ToString();
        hit_data["Mysterious"] = __instance.HasStatusEffectActive( RREnemyStatusEffect.Mysterious ).ToString();

        return hit_data;
    }

    [HarmonyPatch( typeof(RRWyrmEnemy), "ScoreAllRemainingHoldSegments")]
    [HarmonyPrefix]
    public static bool WyrmEnd( RRWyrmEnemy __instance ){
        Dictionary<string, string> hit_data = BasicEnemyEvent(__instance, false);
        hit_data["Event"] = "WyrmEnd";
        hit_events.Add(hit_data);
        return true;
    }

    [HarmonyPatch( typeof(RRWyrmEnemy), "ScoreHoldSegment")]
    [HarmonyPrefix]
    public static bool WyrmSection( RRWyrmEnemy __instance ){
        Dictionary<string, string> hit_data = BasicEnemyEvent(__instance, false);
        hit_data["Event"] = "WyrmSection";
        hit_events.Add(hit_data);
        return true;
    }

    [HarmonyPatch( typeof(RRBlademasterEnemy), "PlayPrepareStrikeAnimationRoutine")]
    [HarmonyPrefix]
    public static bool BBPrepare( RRBlademasterEnemy __instance ){
        Dictionary<string, string> hit_data = BasicEnemyEvent(__instance, false);
        hit_data["Event"] = "BlademasterPrepare";

        hit_events.Add(hit_data);

        return true;
    }

    [HarmonyPatch( typeof(RRSkeletonEnemy), "ProcessIncomingAttack")]
    [HarmonyPrefix]
    public static bool SkeletonFix(RRSkeletonEnemy __instance){
        if( (bool) get_prop_by_name(__instance, "IsHoldingShield") ){
            OnHitEnemy(__instance);
        }
        return true;
    }


    [HarmonyPatch( typeof(RREnemy), "ProcessIncomingAttack")]
    [HarmonyPrefix]
    public static bool OnHitEnemy( RREnemy __instance ){
        
        Dictionary<string, string> hit_data = BasicEnemyEvent(__instance, true);

        hit_data["Event"] = "HitEnemy";

        if( __instance.IsHoldNote ){
            hit_data["Length"] = get_prop_by_name(__instance, "EnemyLength").ToString();
        }  
        
        hit_events.Add(hit_data);
        Logger.LogInfo("HitEnemy");
        return true;
    }

    [HarmonyPatch( typeof(RRStageController), "VibeChainSuccess")]
    [HarmonyPrefix]
    public static bool VCS(){
        Dictionary<string, string> hit_data = new Dictionary<string, string>();

        hit_data["Event"] = "VCS";
        hit_data["Beat"] = update_beat.ToString();
        hit_data["Time"] = update_time.ToString();

        hit_events.Add(hit_data);
        return true;
    }

    [HarmonyPatch(typeof(RRStageController), "HandleDebugUpdate")]
    [HarmonyPrefix]
    public static bool HandleDebugUpdate( ref FmodTimeCapsule __0 ){
        update_beat = __0.TrueBeatNumber;
        update_time = active_beatmap.GetTimeFromBeatNumber( __0.TrueBeatNumber );
        return true;
    }

    [HarmonyPatch( typeof(RRStageController), "ShowResultsScreen")]
    [HarmonyPrefix]
    public static bool OutputJson( RRStageController __instance, StageScenePayload ____stageScenePayload, string ____customTrackAudioFilePath ){
        string level_id = "";
        StageScenePayload _stageScenePayload = ____stageScenePayload;
        if( !string.IsNullOrWhiteSpace( ____customTrackAudioFilePath )  ){
            RRCustomTrackScenePayload rRCustomTrackScenePayload = _stageScenePayload as RRCustomTrackScenePayload;
            level_id = rRCustomTrackScenePayload.GetLevelId();
        } else {
            LevelIdDatabase.Instance.TryGetLevelId(_stageScenePayload.AssetGuid, out level_id);
        }

        int diff = (int)_stageScenePayload.GetLevelDifficulty();

        string path = "Charts/" + level_id + "-"+ diff.ToString() +".json";

        Dictionary<string, object> json = new Dictionary<string, object>();

        json["name"] = level_id;
        json["diff"] = diff;
        json["events"] = hit_events;//JsonConvert.SerializeObject(hit_events, Formatting.None );

        Logger.LogInfo(path);
        File.WriteAllText( path, JsonConvert.SerializeObject(json, Formatting.Indented ) );

        hit_events = [];
        if( playing_dlc ){
            PlayDLC();
        } else {
            PlayNext();
        }        
        return true;
    }

    public static void PlayChart( string level_id, int diff ){
        Logger.LogInfo(String.Format("Playing Chart {0}", level_id));
        var return_payload = ScriptableObject.CreateInstance<TrackSelectionScenePayload>();
        return_payload.SetDestinationScene("TrackSelection");
        Difficulty selected_diff = (Difficulty)diff;

        return_payload.Initialize( level_id, selected_diff, TrackSortingOrder.IntensityAscending, false, false);
        SceneLoadData.SetReturnScenePayload(return_payload);

        var sceneToLoadMetadata = new SceneLoadData.SceneToLoadMetaData() {
            SceneName = "RhythmRift",
            LevelId = level_id,
            StageDifficulty = selected_diff,
            IsStoryMode = false,
            IsCalibrationTest = false,
            IsTutorial = false,
            IsPracticeMode = false,
            PracticeModeStartBeat = 0f,
            PracticeModeEndBeat = 0f,
            PracticeModeSpeedModifier = SpeedModifier.OneHundredPercent,
            IsShopkeeperMode = false,
            IsRemixMode = false,
            CustomRemixSeed = string.Empty,
            IsRandomSeed = false,
            ShouldRewardDiamonds = true,
            ShouldLevelBeLoaded = true,
            RRIntroDialogueID = string.Empty,
            RROutroDialogueID = string.Empty,
            ShouldMuteCounterpartVO = false,
            ShouldInvertCounterpartReactions = false
        };    

        SceneLoadData.StageEntryType = RiftAnalyticsService.StageEntryType.StageSelectMenu;
        SceneLoadData.QueueSceneLoadingMetaData(sceneToLoadMetadata);
        SceneLoadingController.Instance.GoToScene(sceneToLoadMetadata);
    }

    public static void AddDLC( string codename, int count ){
        for( int i = 0; i < count; i++ ){
            Logger.LogInfo(String.Format( "DLC{0}{1:00}", codename, i+1 ));
            dlc_track_names.Add( String.Format( "DLC{0}{1:00}", codename, i+1 ) );
        }
    }

    static bool playing_dlc = false;
    static List<string> dlc_track_names;
    static int dlc_index = 0;
    public static void PlayDLC(){
        if( dlc_index >= dlc_track_names.Count ){ return; }
        var current_track = dlc_track_names[dlc_index];
        PlayChart( current_track, diff+1 );
        diff += 1;
        diff %= 4;
        if( diff == 0 ) dlc_index += 1;
        
    }

    public static void PlayNext(){
        if( track_mode == -1 ) return;
        if( track_index >= tracks.Length ){
            StartDLC();
            return;
        }
        RRTrackMetaData current_track = tracks[track_index];
        while( current_track.IsFiller || current_track.IsTutorial || current_track.IsPromo ){
            track_index += 1;
            if( track_index >= tracks.Length ) {
                StartDLC();
                return;
            }
            current_track = tracks[track_index];
        }
        SongDatabaseData? data;
        SongDatabase.Instance.TryGetEntryForLevelId( current_track.LevelId, out data );

        var return_payload = ScriptableObject.CreateInstance<TrackSelectionScenePayload>();
        return_payload.SetDestinationScene("TrackSelection");
        Difficulty selected_diff = Difficulty.None;
        if( track_mode != 0 ){
            selected_diff = (Difficulty)track_mode;
        } else {
            selected_diff = (Difficulty)(diff+1);
        }
        return_payload.Initialize(current_track.LevelId, selected_diff, TrackSortingOrder.IntensityAscending, false, false);
        SceneLoadData.SetReturnScenePayload(return_payload);

        var sceneToLoadMetadata = new SceneLoadData.SceneToLoadMetaData() {
            SceneName = "RhythmRift",
            LevelId = data?.LevelId ?? string.Empty,
            StageDifficulty = selected_diff,
            IsStoryMode = false,
            IsCalibrationTest = false,
            IsTutorial = false,
            IsPracticeMode = false,
            PracticeModeStartBeat = 0f,
            PracticeModeEndBeat = 0f,
            PracticeModeSpeedModifier = SpeedModifier.OneHundredPercent,
            IsShopkeeperMode = false,
            IsRemixMode = false,
            CustomRemixSeed = string.Empty,
            IsRandomSeed = false,
            ShouldRewardDiamonds = true,
            ShouldLevelBeLoaded = true,
            RRIntroDialogueID = string.Empty,
            RROutroDialogueID = string.Empty,
            ShouldMuteCounterpartVO = false,
            ShouldInvertCounterpartReactions = false
        };    

        if( track_mode != 0 ){
            track_index += 1;
        } else {
            diff += 1;
            diff %= 4;
            if( diff == 0 ) track_index += 1;
        }

        SceneLoadData.StageEntryType = RiftAnalyticsService.StageEntryType.StageSelectMenu;
        SceneLoadData.QueueSceneLoadingMetaData(sceneToLoadMetadata);
        SceneLoadingController.Instance.GoToScene(sceneToLoadMetadata);
    }

    public static void StartReinit(){
        dlc_index = 0;
        diff = 0;
        track_index = 0;
    }
    public static void StartVanilla( int mode ){
        Logger.LogInfo("Init autoplay");
        StartReinit();
        playing_dlc = false;
        track_mode = mode;
        diff = mode;
        Logger.LogInfo("Playing first song.");
        PlayNext();
    }
    public static void StartDLC(){
        Logger.LogInfo("Init DLC autoplay");
        StartReinit();
        playing_dlc = true;
        dlc_track_names = new List<string>();
        AddDLC("Apricot", 3);
        AddDLC("Banana", 5);
        AddDLC("Cherry", 0);
        AddDLC("Dorian", 0);
        Logger.LogInfo("Playing first song.");
        PlayDLC();
    }

    public static RRTrackMetaData[] tracks;
    public static int track_index = 0;
    public static int track_mode = -1;
    public static int diff = 0;

    [HarmonyPatch( typeof(TrackSelectionSceneController), "Update")]
    [HarmonyPrefix]
    public static bool TrackSelectionUpdate( TrackSelectionSceneController __instance, RRTrackDatabase ____trackDatabase ){
        
        if( Input.GetKeyDown(KeyCode.F1)){
            tracks = (RRTrackMetaData[])____trackDatabase.GetTrackMetaDatas().Clone();
            StartVanilla(0);
        }
        if( Input.GetKeyDown(KeyCode.F2)){
            tracks = (RRTrackMetaData[])____trackDatabase.GetTrackMetaDatas().Clone();
            StartVanilla(1);
        }
        if( Input.GetKeyDown(KeyCode.F3)){
            tracks = (RRTrackMetaData[])____trackDatabase.GetTrackMetaDatas().Clone();
            StartVanilla(2);
        }
        if( Input.GetKeyDown(KeyCode.F4)){
            tracks = (RRTrackMetaData[])____trackDatabase.GetTrackMetaDatas().Clone();
            StartVanilla(3);
        }
        if( Input.GetKeyDown(KeyCode.F5)){
            tracks = (RRTrackMetaData[])____trackDatabase.GetTrackMetaDatas().Clone();
            StartVanilla(4);            
        }
        
        if( Input.GetKeyDown(KeyCode.F6)){
            StartDLC();
        }

        return true;
    }
    

}

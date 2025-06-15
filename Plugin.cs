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
using JetBrains.Annotations;
using UnityEngine.Analytics;
using Shared.Pins;
using UnityEngine.Video;

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

    public static float TimeFromBeat( float beat ){
        if( active_beatmap.HasBeatTimings ){
            if( beat > active_beatmap.BeatTimings.Count - 2 ){
                //will break
                double m1 = active_beatmap.BeatTimings[ active_beatmap.BeatTimings.Count - 1 ];
                double m2 = active_beatmap.BeatTimings[ active_beatmap.BeatTimings.Count - 2 ];
                double spb = m1 - m2;
                float amt = beat - (float)(active_beatmap.BeatTimings.Count);
                return (float)(m1 + (amt * spb));
            }
        }
        return active_beatmap.GetTimeFromBeatNumber(beat);
    }

    public static Dictionary<string, string> BasicEnemyEvent( RREnemy __instance, bool is_hit ){
        Dictionary<string, string> hit_data = new Dictionary<string, string>();

        //Indentifiers
        hit_data["GUID"] = __instance.GroupId.ToString();
        hit_data["ID"] = __instance.EnemyTypeId.ToString();
      
        //Extra info for rendering
        hit_data["Facing"] = __instance.IsFacingLeft ? "Left" : "Right";
        hit_data["Health"] = __instance.CurrentHealthValue.ToString();

        //Main timing / position info
        if( is_hit ){
            int2 hit_pos =__instance.CurrentGridPosition.y == 0 ? __instance.CurrentGridPosition : __instance.TargetGridPosition;
            hit_data["X"] = hit_pos.x.ToString();
            hit_data["Y"] = hit_pos.y.ToString();

            hit_data["Beat"] = __instance.TargetHitBeatNumber.ToString();
            hit_data["Time"] = TimeFromBeat( __instance.TargetHitBeatNumber ).ToString();
        } else {
            int2 hit_pos = __instance.CurrentGridPosition;
            hit_data["X"] = hit_pos.x.ToString();
            hit_data["Y"] = hit_pos.y.ToString();

            float beat = __instance.LastUpdateTrueBeatNumber;
            hit_data["Beat"] = beat.ToString();
            hit_data["Time"] = TimeFromBeat( beat ).ToString();
        }

        //Extra timing info
        
        float lastbeat = __instance.LastUpdateTrueBeatNumber;
        hit_data["LastBeat"] = lastbeat.ToString();
        hit_data["LastTime"] = TimeFromBeat( lastbeat ).ToString();

        float nextbeat = __instance.NextUpdateTrueBeatNumber;
        hit_data["NextBeat"] = nextbeat.ToString();
        hit_data["NextTime"] = TimeFromBeat( nextbeat ).ToString();

        hit_data["FrameBeat"] = update_beat.ToString();
        hit_data["FrameTime"] = update_time.ToString();

        hit_data["Spawn"] = __instance.SpawnTrueBeatNumber.ToString();

        //effects
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

    [HarmonyPatch( typeof(RRArmadilloEnemy), "ProcessIncomingAttack")]
    [HarmonyPrefix]
    public static bool ArmadilloFix(RRArmadilloEnemy __instance){
        if( (bool) get_prop_by_name(__instance, "IsHoldingShield") ){
            OnHitEnemy(__instance);
        }
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
        StageScenePayload _stageScenePayload = ____stageScenePayload as RRDynamicScenePayload;
        if( !string.IsNullOrWhiteSpace( ____customTrackAudioFilePath )  ){
            RRDynamicScenePayload rRCustomTrackScenePayload = _stageScenePayload as RRDynamicScenePayload;
            level_id = rRCustomTrackScenePayload.GetLevelId();
        } else {
            LevelIdDatabase.Instance.TryGetLevelId(_stageScenePayload.AssetGuid, out level_id);
        }

        //float intensity = (float)get_field_by_name(_stageScenePayload, "_intensityValue");
        int diff = (int)_stageScenePayload.GetLevelDifficulty();

        string path = "";
        if( PinsController.IsPinActive("GoldenLute") ){
            path = "Charts/" + level_id + "-"+ diff.ToString() +".json";
        } else {
            //getting ready to add misses etc, and preventing regular play overriding actual chart data
            var date = DateTime.Now.ToString();
            date = date.Replace("/", "-");
            date = date.Replace("\\", "-");
            path = "History/" + level_id + "-" + diff.ToString() + "("+date +").json";
        }
        

        Dictionary<string, object> json = new Dictionary<string, object>();

        json["name"] = level_id;
        json["diff"] = diff;
        json["int_name"] = active_beatmap.name;
        json["bpm"] = active_beatmap.bpm;
        json["beatDivisions"] = active_beatmap.beatDivisions;
        json["intensity"] = intensity;
        json["beatTimings"] = active_beatmap.BeatTimings;

        List<List<double>> bpm_list = new List<List<double>>();
        foreach( BeatmapEvent e in active_beatmap.BeatmapEvents ){
            if( e.type != "AdjustBPM" ) continue;
            double beat = (float)e.startBeatNumber;
            double new_bpm = (float)e.GetFirstEventDataAsFloat("Bpm");
            List<double> pair = new List<double>{beat, new_bpm};
            bpm_list.Add(pair);            
        }

        json["BpmEvents"] = bpm_list;

        json["events"] = hit_events;//JsonConvert.SerializeObject(hit_events, Formatting.None );


        Logger.LogInfo(path);
        File.WriteAllText( path, JsonConvert.SerializeObject( json, Formatting.Indented ) );

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

        SongDatabaseData? data;
        SongDatabase.Instance.TryGetEntryForLevelId( current_track, out data );
        intensity = data?.DifficultyInformation?[diff].ChallengeRating ?? 0;

        PlayChart( current_track, diff+1 );
        diff += 1;
        diff %= 4;
        if( diff == 0 ) dlc_index += 1;
        
    }

    public static void PlayNext()
    {
        if (track_mode == -1) return;
        if (track_index >= tracks.Length)
        {
            StartDLC();
            return;
        }
        Logger.LogInfo("PN 0");
        RRTrackMetaData current_track = tracks[track_index];
        while (current_track.IsFiller || current_track.IsTutorial || current_track.IsPromo)
        {
            track_index += 1;
            if (track_index >= tracks.Length)
            {
                StartDLC();
                return;
            }
            current_track = tracks[track_index];
        }
        Logger.LogInfo("PN 1");
        SongDatabaseData? data;
        SongDatabase.Instance.TryGetEntryForLevelId(current_track.LevelId, out data);

        Logger.LogInfo("PN 2");
        var return_payload = ScriptableObject.CreateInstance<TrackSelectionScenePayload>();
        return_payload.SetDestinationScene("TrackSelection");
        Difficulty selected_diff = Difficulty.None;
        if (track_mode != 0)
        {
            selected_diff = (Difficulty)track_mode;
        }
        else
        {
            selected_diff = (Difficulty)(diff + 1);
        }
        Logger.LogInfo("PN 3");
        intensity = data?.DifficultyInformation?[diff].ChallengeRating ?? 0;
        return_payload.Initialize(current_track.LevelId, selected_diff, TrackSortingOrder.IntensityAscending, false, false);
        SceneLoadData.SetReturnScenePayload(return_payload);
        Logger.LogInfo("PN 4");
        var sceneToLoadMetadata = new SceneLoadData.SceneToLoadMetaData()
        {
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
        Logger.LogInfo("PN 5");
        if (track_mode != 0)
        {
            track_index += 1;
        }
        else
        {
            diff += 1;
            diff %= 4;
            if (diff == 0) track_index += 1;
        }
        Logger.LogInfo("PN 6");
        SceneLoadData.StageEntryType = RiftAnalyticsService.StageEntryType.StageSelectMenu;
        SceneLoadData.QueueSceneLoadingMetaData(sceneToLoadMetadata);
        SceneLoadingController.Instance.GoToScene(sceneToLoadMetadata);
        Logger.LogInfo("PN 7");
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

    public static float intensity;
    [HarmonyPatch( typeof(IntensityMeter), "SetChallengeRating")]
    [HarmonyPrefix]
    public static bool UserUpdateIntensity( float __0 ){
        intensity = __0;
        return true;
    }


    public static RRTrackMetaData[] tracks;
    public static int track_index = 0;
    public static int track_mode = -1;
    public static int diff = 0;

    [HarmonyPatch( typeof(TrackSelectionSceneController), "Update")]
    [HarmonyPrefix]
    public static bool TrackSelectionUpdate( TrackSelectionSceneController __instance, RRTrackDatabase ____trackDatabase, Dictionary<string, Sprite> ____albumArtLargeSprites ){
        
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

        if( Input.GetKeyDown(KeyCode.F7)){
            foreach( string key in ____albumArtLargeSprites.Keys ){
                Sprite s = ____albumArtLargeSprites[key];
                byte[] buffer = TextureReadHack(s.texture).EncodeToPNG();

                string path = "AlbumArts/" + key + ".png";
                FileStream output = new FileStream( path, FileMode.Create, FileAccess.ReadWrite );
                BinaryWriter bw = new BinaryWriter(output);
                bw.Write( buffer );
                bw.Close();
            }
        }

        return true;
    }

    public static Texture2D TextureReadHack(Texture2D in_tex)
    {
        RenderTexture temporary = RenderTexture.GetTemporary(in_tex.width, in_tex.height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Linear);
        Graphics.Blit(in_tex, temporary);
        RenderTexture active = RenderTexture.active;
        RenderTexture.active = temporary;
        Texture2D texture2D = new Texture2D(in_tex.width, in_tex.height);
        texture2D.ReadPixels(new Rect(0f, 0f, (float)temporary.width, (float)temporary.height), 0, 0);
        texture2D.Apply();
        RenderTexture.active = active;
        RenderTexture.ReleaseTemporary(temporary);
        return texture2D;
    }
    
    [HarmonyPatch(typeof(Beatmap), "LoadFromJsonString")]
    [HarmonyPrefix]
    public static bool LoadBeatmapFromJson( string __0, string __1 ){
        string level_id = __1;
        if( level_id.EndsWith(".json") ){
            //custom chart
            level_id = Path.GetFileNameWithoutExtension( __1 );
        }
        string path = "ChartDumps/" + level_id + ".json";
        File.WriteAllText( path, __0 );

        return true;
    }

}

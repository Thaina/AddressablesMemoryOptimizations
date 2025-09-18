#if UNITY_EDITOR
/// This AnalyzeRule is based on the built-in rule CheckBundleDupeDependencies
/// This rule finds assets in Addressables that will be duplicated across multiple AssetBundles
/// Instead of placing all problematic assets in a shared Group, this rule results in fewer AssetBundles
/// being created by placing assets with the same AssetBundle parents into the same label and AssetBundle
using System;
using System.Linq;
using System.Collections.Generic;

using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Build.Pipeline;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.AnalyzeRules;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

class CheckBundleDupeDependenciesV2 : BundleRuleBase
{
    public override bool CanFix => true;

    public override string ruleName => "Check Duplicate Bundle Dependencies V2";

    internal Dictionary<HashSet<string>, List<GUID>> duplicateAssetsByParents = new(HashSet<string>.CreateSetComparer());

    // The function that is called when the user clicks "Analyze Selected Rules" in the Analyze window
    public override List<AnalyzeResult> RefreshAnalysis(AddressableAssetSettings settings)
    {
        ClearAnalysis();
        return CheckForDuplicateDependencies(settings);
    }

    List<AnalyzeResult> CheckForDuplicateDependencies(AddressableAssetSettings settings)
    {
        if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.LogWarning("Cannot run Analyze with unsaved scenes");
            return new() { new AnalyzeResult() { severity = MessageType.Warning,resultName = ruleName + "Cannot run Analyze with unsaved scenes" } };
        }

        CalculateInputDefinitions(settings);

        if(AllBundleInputDefs.Count > 0)
        {
            var context = GetBuildContext(settings);
            ReturnCode exitCode = RefreshBuild(context);
            if(exitCode < ReturnCode.Success)
            {
                Debug.LogError("Analyze build failed. " + exitCode);
                return new() { new AnalyzeResult() { severity = MessageType.Error,resultName = ruleName + " Analyze build failed. " + exitCode } };
            }

            List<AnalyzeResult> retVal = CheckForDuplicateDependencies(context);
            if(retVal.Count > 0)
                return retVal;
        }

        return new() { noErrors };
    }

    List<AnalyzeResult> CheckForDuplicateDependencies(AddressableAssetsBuildContext context)
    {
        var validGuids = GetImplicitGuidToFilesMap().Select((dupeGuid) => {
            return (guid: dupeGuid.Key,path: AssetDatabase.GUIDToAssetPath(dupeGuid.Key), assetParents: dupeGuid.Value.Distinct().ToHashSet());
        }).Where((pair) => {
            return IsValidPath(pair.path) && pair.assetParents.Count > 1;
        }).ToList();

        // Key = a set of bundle parents
        // Value = asset paths that share the same bundle parents
        // e.g. <{"bundle1", "bundle2"} , {"Assets/Sword_D.tif", "Assets/Sword_N.tif"}>
        duplicateAssetsByParents.Clear();
        foreach (var guidToFile in validGuids.GroupBy((pair) => pair.assetParents,(pair) => pair.guid,HashSet<string>.CreateSetComparer()))
            duplicateAssetsByParents.Add(guidToFile.Key,guidToFile.ToList());

        return validGuids.SelectMany((guidToFile) => {
            return guidToFile.assetParents.Join(ExtractData.WriteData.FileToBundle,(file) => file,(pair) => pair.Key,(file,pair) => {
                return (fileToBundle: pair.Value, assetPath: guidToFile.path);
            });
        }).GroupBy((dupeResult) => dupeResult.fileToBundle,(dupeResult) => dupeResult.assetPath).GroupBy((dupeResultGroup) => {
            string bundleToGroup = context.bundleToAssetGroup[dupeResultGroup.Key];
            var group = context.Settings.FindGroup(findGroup => findGroup != null && findGroup.Guid == bundleToGroup);
            return group.Name;
        }).SelectMany((issueGroup) => {
            return issueGroup.SelectMany((bundle) => {
                string bundleName = ConvertBundleName(bundle.Key,issueGroup.Key);
                return bundle.Select((item) => {
                    return new AnalyzeResult() {
                        resultName = string.Join(kDelimiter,issueGroup.Key,bundleName,item),
                        severity = MessageType.Warning
                    };
                });
            });
        }).ToList();
    }

    // The function that is called when the user clicks "Fix Issues" in the Analyze window
    public override void FixIssues(AddressableAssetSettings settings)
    {
        if(duplicateAssetsByParents == null)
            duplicateAssetsByParents = new();

        if(duplicateAssetsByParents.Count == 0)
            CheckForDuplicateDependencies(settings);

        // If we have found no duplicates, return
        if(duplicateAssetsByParents.Count == 0)
            return;

        // Setup a new Addressables Group to store all our duplicate assets
        string desiredGroupName = "Duplicate Assets Sorted By Label";
        AddressableAssetGroup group = settings.FindGroup(desiredGroupName);
        if (group == null)
        {
            group = settings.CreateGroup(desiredGroupName, false, false, false, null!, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            var bundleSchema = group.GetSchema<BundledAssetGroupSchema>();
            // Set to pack by label so that assets with the same label are put in the same AssetBundle
            bundleSchema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogetherByLabel;
        }

        EditorUtility.DisplayProgressBar("Setting up De-Duplication Group...", "", 0f / duplicateAssetsByParents.Count);
        // Iterate through each duplicate asset
        int bundleNumber = 1;
        foreach (var (parents,entry) in duplicateAssetsByParents)
        {
            EditorUtility.DisplayProgressBar("Setting up De-Duplication Group...", "Creating Label Group", ((float)bundleNumber) / duplicateAssetsByParents.Count);
            // Create a new Label
            string desiredLabelName = "Bundle" + bundleNumber;
            settings.AddLabel(desiredLabelName);
            List<AddressableAssetEntry> entriesToAdd = entry.Select((guid) => {
                // Set the label for this selection of assets so they get packed into the same AssetBundle
                var e = settings.CreateOrMoveEntry(guid.ToString(),group,false,false);
                e.SetLabel(desiredLabelName,true,false);
                return e;
            }).ToList();

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryModified, entriesToAdd, true, true);

            bundleNumber++;
        }

        settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null!, true, true);
    }

    // The function that is run when the user clicks "Clear Selected Rules" in the Analyze window
    public override void ClearAnalysis()
    {
        duplicateAssetsByParents.Clear();
        base.ClearAnalysis();
    }

    [InitializeOnLoadMethod]
    static void Register() => AnalyzeSystem.RegisterNewRule<CheckBundleDupeDependenciesV2>();
}
#endif

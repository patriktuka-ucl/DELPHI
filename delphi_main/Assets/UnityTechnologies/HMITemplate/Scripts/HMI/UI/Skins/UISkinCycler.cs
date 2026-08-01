using HMI.UI.Skins;
using HMI.UI.Skins.Data;
using System.Collections.Generic;
using UnityEngine;
using System.Linq; // Required for ToList() and OrderBy()

#if UNITY_EDITOR
using UnityEditor; // Required for AssetDatabase, which is editor-only
#endif

namespace HMI.UI.Skins
{
    /// <summary>
    /// Allows cycling through available UI skins during runtime using keyboard input.
    /// Press 'Y' to cycle to the next skin and 'T' to cycle to the previous skin.
    /// </summary>
    public class UISkinCycler : MonoBehaviour
    {
        // This list is primarily for editor visibility and initial setup.
        // For builds, skins are loaded from the Resources folder.
        [Tooltip("This list is automatically populated in the editor for convenience. For runtime builds, skins are loaded from any 'Resources' folder in the project.")]
        public List<UISkinCollectionData> AvailableSkins = new List<UISkinCollectionData>();

        private UISkinCollectionSwapper _skinSwapper;
        private int _currentSkinIndex = -1;

#if UNITY_EDITOR
        /// <summary>
        /// Automatically populates the AvailableSkins list in the editor for quick overview.
        /// This method is called in the editor when the script is loaded or a value changes in the Inspector.
        /// </summary>
        private void OnValidate()
        {
            if (AvailableSkins == null)
            {
                AvailableSkins = new List<UISkinCollectionData>();
            }

            // Clear and repopulate the list to ensure it's always up-to-date with project assets
            AvailableSkins.Clear();
            var guids = AssetDatabase.FindAssets("t:UISkinCollectionData");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var skinData = AssetDatabase.LoadAssetAtPath<UISkinCollectionData>(path);
                if (skinData != null)
                {
                    AvailableSkins.Add(skinData);
                }
            }
            // Sort them by name for consistent ordering when cycling
            AvailableSkins = AvailableSkins.OrderBy(s => s.name).ToList();

            // Set the initial index for editor use, this value is NOT serialized for builds
            InitializeCurrentSkinIndex(editorInitialization: true);
        }
#endif

        private void Awake()
        {
            Debug.Log("[UISkinCycler] Awake method called.", this);

            _skinSwapper = FindObjectOfType<UISkinCollectionSwapper>();

            if (_skinSwapper == null)
            {
                Debug.LogError("[UISkinCycler] UISkinCollectionSwapper not found in the scene. Please add one to enable skin cycling.", this);
                enabled = false;
                return;
            }
            Debug.Log("[UISkinCycler] UISkinCollectionSwapper found: " + _skinSwapper.name, _skinSwapper);


            // At runtime, load all UISkinCollectionData from Resources folders
            // This ensures they are included in the build and accessible.
            var loadedSkinsArray = Resources.LoadAll<UISkinCollectionData>("");
            AvailableSkins = loadedSkinsArray.ToList();
            // Sort them again to ensure consistent cycling order at runtime
            AvailableSkins = AvailableSkins.OrderBy(s => s.name).ToList();

            Debug.Log($"[UISkinCycler] Found {AvailableSkins.Count} skins via Resources.LoadAll().", this);
            if (AvailableSkins.Count > 0)
            {
                foreach(var skin in AvailableSkins)
                {
                    Debug.Log($"[UISkinCycler] Loaded skin: {skin?.name ?? "NULL_SKIN"}", skin);
                }
            }


            if (AvailableSkins == null || AvailableSkins.Count == 0)
            {
                Debug.LogWarning("[UISkinCycler] No UISkinCollectionData assets found in 'Resources' folders. Please place your skin assets into a folder named 'Resources' to make them available in builds.", this);
                enabled = false;
                return;
            }

            // Initialize _currentSkinIndex at runtime
            InitializeCurrentSkinIndex();

            // Apply the initial skin if it's different from the swapper's current skin
            if (_currentSkinIndex != -1 && _skinSwapper.SkinCollectionData != AvailableSkins[_currentSkinIndex])
            {
                Debug.Log($"[UISkinCycler] Initial skin on swapper '{_skinSwapper.SkinCollectionData?.name ?? "None"}' differs from intended start '{AvailableSkins[_currentSkinIndex]?.name ?? "None"}'. Applying initial skin.", this);
                ApplyCurrentSkin();
            }
            else if (_currentSkinIndex != -1)
            {
                Debug.Log($"[UISkinCycler] Initial skin '{AvailableSkins[_currentSkinIndex]?.name ?? "None"}' already set on swapper.", this);
            }
        }

        private void InitializeCurrentSkinIndex(bool editorInitialization = false)
        {
            if (_skinSwapper != null && _skinSwapper.SkinCollectionData != null)
            {
                _currentSkinIndex = AvailableSkins.IndexOf(_skinSwapper.SkinCollectionData);
                if (_currentSkinIndex == -1 && AvailableSkins.Count > 0)
                {
                    Debug.LogWarning($"[UISkinCycler] The currently active skin on UISkinCollectionSwapper ('{_skinSwapper.SkinCollectionData.name}') is not found in the AvailableSkins list. Starting with the first skin.", this);
                    _currentSkinIndex = 0;
                }
            }
            else if (AvailableSkins.Count > 0)
            {
                _currentSkinIndex = 0; // Fallback to the first skin if no swapper skin is set
                if (editorInitialization) Debug.Log("[UISkinCycler] Swapper has no initial skin or not found, defaulting to first available skin in editor.", this);
            }
            else
            {
                _currentSkinIndex = -1; // No skins available to set
                if (editorInitialization) Debug.LogWarning("[UISkinCycler] No skins available to set _currentSkinIndex in editor.", this);
            }

            if (_currentSkinIndex != -1)
            {
                Debug.Log($"[UISkinCycler] Initialized _currentSkinIndex to {_currentSkinIndex} (Skin: {AvailableSkins[_currentSkinIndex]?.name ?? "None"}).", this);
            }
        }
        /// <summary>
        /// Cycles to the next UI skin. This method is intended to be called by the ControlsManager.
        /// </summary>
        public void CycleNextSkin()
        {
            if (AvailableSkins == null || AvailableSkins.Count <= 1)
            {
                //Debug.Log("[UISkinCycler] Not enough skins to cycle or list is null/empty.", this);
                return;
            }

            _currentSkinIndex++;
            if (_currentSkinIndex >= AvailableSkins.Count)
            {
                _currentSkinIndex = 0; // Wrap around to the first skin
            }
            Debug.Log($"[UISkinCycler] Cycling to next skin (index: {_currentSkinIndex}) via ControlsManager.", this);
            ApplyCurrentSkin();
        }

        /// <summary>
        /// Cycles to the previous UI skin. This method is intended to be called by the ControlsManager.
        /// </summary>
        public void CyclePreviousSkin()
        {
            if (AvailableSkins == null || AvailableSkins.Count <= 1)
            {
                //Debug.Log("[UISkinCycler] Not enough skins to cycle or list is null/empty.", this);
                return;
            }

            _currentSkinIndex--;
            if (_currentSkinIndex < 0)
            {
                _currentSkinIndex = AvailableSkins.Count - 1; // Wrap around to the last skin
            }
            Debug.Log($"[UISkinCycler] Cycling to previous skin (index: {_currentSkinIndex}) via ControlsManager.", this);
            ApplyCurrentSkin();
        }

        /// <summary>
        /// Applies the skin at the currentSkinIndex to the UISkinCollectionSwapper.
        /// </summary>
        private void ApplyCurrentSkin()
        {
            if (_skinSwapper != null && AvailableSkins != null && _currentSkinIndex >= 0 && _currentSkinIndex < AvailableSkins.Count)
            {
                UISkinCollectionData selectedSkin = AvailableSkins[_currentSkinIndex];
                if (selectedSkin != null)
                {
                    _skinSwapper.SetSkin(selectedSkin);
                    Debug.Log($"[UISkinCycler] Successfully attempted to switch to skin: {selectedSkin.name} (Index: {_currentSkinIndex})", this);
                }
                else
                {
                    Debug.LogWarning($"[UISkinCycler] UISkinCollectionData at index {_currentSkinIndex} is null in AvailableSkins list. Cannot apply null skin.", this);
                }
            }
            else
            {
                Debug.LogError("[UISkinCycler] Failed to apply skin. Check if _skinSwapper is assigned, AvailableSkins is populated, and _currentSkinIndex is valid. Current state: _skinSwapper=" + (_skinSwapper != null) + ", AvailableSkins.Count=" + (AvailableSkins?.Count ?? 0) + ", _currentSkinIndex=" + _currentSkinIndex, this);
            }
        }
    }
}

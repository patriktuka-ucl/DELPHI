using UnityEditor;
using UnityEngine;

namespace Delphi.EditorTools
{
    /// <summary>
    /// Auto-configures Assets/QuestionnaireToolkit/Textures/Generated/
    /// RoundedPanel.png as a 9-sliced UI Sprite the moment Unity imports it —
    /// no manual Inspector steps. The PNG itself (a white rounded-rect with
    /// alpha falloff at the corners, 128×128, 32px corner radius) was
    /// generated procedurally; this just tells Unity how to slice it so the
    /// corners stay a fixed size while the flat edges/center stretch to fit
    /// any panel size. Drag it into a QTQuestionnaireManager page's
    /// background Image.sprite (Image Type: Sliced) for rounded corners.
    /// </summary>
    public class RoundedPanelSpriteImporter : AssetPostprocessor
    {
        private const string TargetPath = "Assets/QuestionnaireToolkit/Textures/Generated/RoundedPanel.png";

        private void OnPreprocessTexture()
        {
            if (assetPath != TargetPath) return;

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spriteBorder = new Vector4(32, 32, 32, 32); // matches the 32px corner radius baked into the PNG
            importer.filterMode = FilterMode.Bilinear;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
        }
    }
}

using UnityEditor;
using UnityEngine;
using QuestionnaireToolkit.Scripts;

namespace Delphi.EditorTools
{
    /// <summary>
    /// One-click "add N options" for a QTLinearScale question item. The
    /// toolkit's own Inspector only offers "Add Option" one at a time
    /// (QTLinearScaleEditor) — fine for a 5- or 7-point scale, tedious and
    /// error-prone for a 21-point one. AddOption() already auto-numbers
    /// sequentially when called with no arguments, so this is just that same
    /// call in a loop.
    /// </summary>
    public static class QTLinearScaleBulkOptionsEditor
    {
        [MenuItem("DELPHI/Questionnaire/Set Selected Linear Scale to 21 Points")]
        private static void SetSelectedTo21Points() => SetSelectedTo(21);

        [MenuItem("DELPHI/Questionnaire/Set Selected Linear Scale to 21 Points", true)]
        private static bool ValidateSelection() =>
            Selection.activeGameObject != null &&
            Selection.activeGameObject.GetComponent<QTLinearScale>() != null;

        private static void SetSelectedTo(int targetCount)
        {
            var linearScale = Selection.activeGameObject.GetComponent<QTLinearScale>();
            if (linearScale == null)
            {
                Debug.LogWarning("[DELPHI] Select a GameObject with a QTLinearScale component first.");
                return;
            }

            int existing = linearScale.options?.Count ?? 0;
            if (existing >= targetCount)
            {
                Debug.Log($"[DELPHI] '{linearScale.name}' already has {existing} options (>= {targetCount}) — nothing to add.");
                return;
            }

            Undo.RegisterFullObjectHierarchyUndo(linearScale.gameObject, "Set Linear Scale to 21 Points");
            int toAdd = targetCount - existing;
            for (int i = 0; i < toAdd; i++)
                linearScale.AddOption();

            EditorUtility.SetDirty(linearScale);
            Debug.Log($"[DELPHI] '{linearScale.name}': added {toAdd} option(s), now {linearScale.options.Count} total.");
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace QuestionnaireToolkit.Scripts
{
    // Minimal stand-ins for real QuestionnaireToolkit types, kept only so
    // Assets/BOforUnity's Scripts/Examples (never wired into the live Delphi
    // scene, not touched by this cleanup) keep compiling now that the actual
    // QuestionnaireToolkit package has been removed as unused. No real
    // questionnaire behavior here — this project's live questionnaire flow is
    // DelphiQuestionnaire/VrQuestionnairePanel, and its actual optimizer is
    // BoBridge + mobo.py, neither of which touch these types.

    public class QTQuestionnaireManager : MonoBehaviour
    {
        public List<GameObject> questionPages = new();
        public string contextUserId, contextConditionId, contextGroupId;

        public bool StartQuestionnaire() => false;

        public void EnsureAdditionalCsvItem(string header, GameObject target,
            string componentTypeName, string memberName, int order)
        { }

        public void BuildHeaderItems() { }

        public void ResolveBoContextForLogging(out string userId, out string conditionId, out string groupId)
        {
            userId = contextUserId;
            conditionId = contextConditionId;
            groupId = contextGroupId;
        }
    }

    public class QTQuestionPageManager : MonoBehaviour
    {
        public List<GameObject> questionItems = new();
    }

    public class QTSlider : MonoBehaviour
    {
        public string headerName;
    }
}

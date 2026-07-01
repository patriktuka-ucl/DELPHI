using UnityEngine;

namespace Delphi
{
    /// <summary>
    /// Root of the sensor hierarchy. Gives Unity a concrete MonoBehaviour type
    /// that ScalarSensor and FrameSensor can both subclass, so Inspector slots
    /// typed as BaseSensor accept any sensor via native drag-and-drop.
    /// </summary>
    public abstract class BaseSensor : MonoBehaviour { }
}

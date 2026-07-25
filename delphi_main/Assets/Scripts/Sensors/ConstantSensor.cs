namespace Delphi
{
    /// <summary>
    /// Emits a fixed constant every sample. Used by DelphiManager's Debug
    /// Constant Feed to drive the whole acquisition / BO pipeline with no real
    /// hardware — NOT meant to be placed in scenes by hand; DelphiManager
    /// spawns one hidden instance at runtime while debug mode is on.
    /// </summary>
    public class ConstantSensor : ScalarSensor
    {
        public float value = 3f;

        public override float Current { get; protected set; } = float.NaN;

        // Runs on DelphiCore's sampling thread — just latches the constant.
        public override float ReadValue() { Current = value; return Current; }
    }
}

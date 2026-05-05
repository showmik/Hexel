namespace Hexprite.Core
{
    /// <summary>
    /// Replaces the bool? drawState tri-state that was passed to ProcessToolInput.
    /// None means no mouse button is pressed (e.g. on MouseUp or preview moves).
    /// </summary>
    public enum DrawMode
    {
        None,
        Draw,
        Erase
    }
}

namespace CaptionRelay.Captions
{
    public interface ICaptionSource
    {
        /// <summary>Normalized caption text ("" = nothing shown), or null when captions aren't available.</summary>
        string? Read();
    }
}

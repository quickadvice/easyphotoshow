namespace EasyPhotoShow.App.Session;

// V1 trial enforcement. No licensing system yet, so every install is treated as trial.
// Documented as a pre-launch open item in the product spec.
public static class TrialLimits
{
    // STRESS TEST values — both MaxPhotos AND MaxDuration MUST be restored to 50 photos / 5 min
    // before any commercial release or public distribution. These elevated limits exist solely
    // to unblock large-collection performance testing during V1 tuning. Shipping with these
    // values would effectively disable the trial gate.
    public const int MaxPhotos = 4000;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(6000);

    public static bool ExceedsPhotoCap(int photoCount) => photoCount > MaxPhotos;

    public static bool ExceedsDurationCap(int photoCount, double secondsPerPhoto)
        => TimeSpan.FromSeconds(photoCount * secondsPerPhoto) > MaxDuration;
}

namespace SliceMcp;

/// <summary>Pure coordinate conversion; no camera access or motor commands.</summary>
public static class CameraGeometry
{
    public static void Finite(double value, string name)
    {
        if (!double.IsFinite(value)) throw new ArgumentException($"{name} must be finite.");
    }

    public static int Align(double value, int min, int max, int reportedStep)
    {
        Finite(value, nameof(value));
        if (max < min) throw new ArgumentException("Invalid driver range.");
        long step = Math.Max(1, reportedStep);
        long last = ((long)max - min) / step;
        long index = (long)Math.Round((Math.Clamp(value, min, max) - min) / step, MidpointRounding.AwayFromZero);
        return (int)(min + Math.Clamp(index, 0, last) * step);
    }

    public static double ZoomPercent(int value, int min, int max) =>
        max > min ? Math.Clamp(100d * ((double)value - min) / ((double)max - min), 0, 100) : 0;

    public static (double Horizontal, double Vertical) EstimatedFov(double zoomPercent)
    {
        Finite(zoomPercent, nameof(zoomPercent));
        // EagleEye IV USB datasheet endpoints, with an ESTIMATED focal-length interpolation.
        // Driver zoom position is not an empirical focal-length calibration.
        double p = Math.Clamp(zoomPercent, 0, 100) / 100;
        double Interpolate(double wide, double tele)
        {
            double wideTan = Math.Tan(wide * Math.PI / 360);
            double teleTan = Math.Tan(tele * Math.PI / 360);
            return 360 / Math.PI * Math.Atan(wideTan / (1 + p * (wideTan / teleTan - 1)));
        }
        return (Interpolate(72.5, 6.9), Interpolate(44.8, 3.9));
    }

    public static (double Pan, double Tilt) CenterOffsets(double imageX, double imageY, double horizontalFov, double verticalFov)
    {
        Finite(imageX, nameof(imageX)); Finite(imageY, nameof(imageY));
        Finite(horizontalFov, nameof(horizontalFov)); Finite(verticalFov, nameof(verticalFov));
        if (Math.Abs(imageX) > 1 || Math.Abs(imageY) > 1) throw new ArgumentException("Image coordinates must be -1..+1; x right, y down, center (0,0).");
        if (horizontalFov <= 0 || horizontalFov >= 179 || verticalFov <= 0 || verticalFov >= 179)
            throw new ArgumentException("Field of view must be greater than 0 and less than 179 degrees.");
        return (180 / Math.PI * Math.Atan(imageX * Math.Tan(horizontalFov * Math.PI / 360)),
            -180 / Math.PI * Math.Atan(imageY * Math.Tan(verticalFov * Math.PI / 360)));
    }
}

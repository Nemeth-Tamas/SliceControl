using SliceMcp;

int checks = 0;
void Equal(double expected, double actual, string message, double tolerance = 1e-8)
{
    if (Math.Abs(expected - actual) > tolerance || !double.IsFinite(actual)) throw new Exception($"{message}: expected {expected}, got {actual}");
    checks++;
}
void Reject(Action action, string message)
{
    try { action(); } catch (ArgumentException) { checks++; return; }
    throw new Exception(message + ": expected argument rejection");
}

Equal(169, CameraGeometry.Align(169, -170, 170, 0), "EagleEye zero pan step falls back to one degree");
Equal(-29, CameraGeometry.Align(-29, -30, 90, 0), "EagleEye zero tilt step falls back to one degree");
Equal(170, CameraGeometry.Align(200, -170, 170, 1), "Pan clamps at upper hardware stop");
Equal(-30, CameraGeometry.Align(-200, -30, 90, 1), "Tilt clamps at lower hardware stop");
Equal(90, CameraGeometry.Align(95, 0, 95, 10), "Nonaligned upper bound never creates an invalid native step");
Equal(0, CameraGeometry.Align(0.4, -170, 170, 1), "Sub-degree correction respects driver precision");
Equal(100, CameraGeometry.ZoomPercent(5680, 0, 5680), "Native zoom maximum maps to 100 percent");
Equal(50, CameraGeometry.ZoomPercent(2840, 0, 5680), "Native zoom midpoint maps to 50 percent");
Equal(0, CameraGeometry.ZoomPercent(7, 7, 7), "Degenerate zoom range cannot divide by zero");

var wide = CameraGeometry.EstimatedFov(0);
var tele = CameraGeometry.EstimatedFov(100);
var middle = CameraGeometry.EstimatedFov(50);
Equal(72.5, wide.Horizontal, "USB model wide horizontal FOV");
Equal(44.8, wide.Vertical, "USB model wide vertical FOV");
Equal(6.9, tele.Horizontal, "USB model tele horizontal FOV");
Equal(3.9, tele.Vertical, "USB model tele vertical FOV");
if (!(wide.Horizontal > middle.Horizontal && middle.Horizontal > tele.Horizontal)) throw new Exception("Zoom must narrow FOV");
checks++;
var origin = CameraGeometry.CenterOffsets(0, 0, wide.Horizontal, wide.Vertical);
Equal(0, origin.Pan, "Image center does not pan"); Equal(0, origin.Tilt, "Image center does not tilt");
var lowerRight = CameraGeometry.CenterOffsets(1, 1, wide.Horizontal, wide.Vertical);
Equal(wide.Horizontal / 2, lowerRight.Pan, "Right image edge pans right by half horizontal FOV");
Equal(-wide.Vertical / 2, lowerRight.Tilt, "Lower image edge tilts downward by half vertical FOV");
var upperLeft = CameraGeometry.CenterOffsets(-1, -1, wide.Horizontal, wide.Vertical);
Equal(-lowerRight.Pan, upperLeft.Pan, "Horizontal centering is symmetric");
Equal(-lowerRight.Tilt, upperLeft.Tilt, "Vertical centering is symmetric");
Reject(() => CameraGeometry.CenterOffsets(1.01, 0, 72.5, 44.8), "Out-of-image target");
Reject(() => CameraGeometry.CenterOffsets(double.NaN, 0, 72.5, 44.8), "NaN target");
Reject(() => CameraGeometry.CenterOffsets(0, double.PositiveInfinity, 72.5, 44.8), "Infinite target");
Reject(() => CameraGeometry.CenterOffsets(0, 0, 180, 44.8), "Impossible perspective FOV");
Reject(() => CameraGeometry.Align(double.NaN, -170, 170, 0), "NaN motor value");
Reject(() => CameraGeometry.Align(0, 20, -20, 1), "Reversed hardware limits");
Console.WriteLine($"PASS: {checks} camera geometry, bounds, driver-quirk and validation checks.");

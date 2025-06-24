namespace SmartData.Tables
{
    public enum InterpolationMethod
    {
        None,     // No interpolation, return exact values only
        Linear,   // Linear interpolation between points
        Nearest,  // Nearest neighbor (take closest value)
        Previous, // Use previous value
        Next      // Use next value
    }
}

using WeatherSynth.Statistics;

namespace WeatherSynth.Wind;

/// <summary>
/// Spreads generated days of wind across their hours.
///
/// <para>Per day, in date order:</para>
/// <list type="number">
/// <item>An hourly AR(1) latent at <see cref="HourlyWindModel.Persistence"/>, continuous across
/// midnight.</item>
/// <item>Standardised over the day's 24 hours, mixed with the month's diurnal pattern at
/// <see cref="HourlyWindModel.DiurnalWeight"/>, and standardised again - so the ordering is the only
/// thing this step decides.</item>
/// <item>Pushed through the day's own within-day Weibull from <see cref="IntradayShapeModel"/>,
/// which sets how widely the hours spread and so what energy the day carries.</item>
/// <item>Rescaled so the 24 hours average <i>exactly</i> to the day's mean speed.</item>
/// </list>
///
/// <para><b>Never touches the daily stream.</b> It draws from a <see cref="Random"/> of its own,
/// seeded through <see cref="StreamSeed"/>, so generating hours cannot move a single daily value.</para>
///
/// <para><b>Not thread-safe</b>: the latent carries state. One per run, and
/// <see cref="Reset"/> between runs.</para>
/// </summary>
internal sealed class HourlyWindGenerator
{
    private readonly HourlyWindModel _model;
    private readonly IntradayShapeModel _shape;
    private readonly double[] _latentDay = new double[HourlyWindModel.HoursPerDay];

    private double _latent;
    private bool _started;

    /// <param name="model">Diurnal patterns and hourly persistence.</param>
    /// <param name="shape">The within-day distribution each day's hours are drawn through.</param>
    public HourlyWindGenerator(HourlyWindModel model, IntradayShapeModel shape)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _shape = shape ?? throw new ArgumentNullException(nameof(shape));
    }

    /// <summary>
    /// The seed of the hourly stream for a run seeded with <paramref name="seed"/>.
    ///
    /// <para>The third draw of <c>new Random(seed)</c>: deterministic across processes, unlike
    /// <see cref="HashCode"/>, and distinct from the two values <c>WeatherSeeds.Split</c> hands
    /// out, so no caller combination makes the hourly stream replay a daily one.</para>
    /// </summary>
    public static int StreamSeed(int seed)
    {
        var streams = new Random(seed);
        streams.Next();
        streams.Next();

        return streams.Next();
    }

    /// <summary>Starts a fresh run, forgetting the previous hour.</summary>
    public void Reset() => _started = false;

    /// <summary>
    /// Writes one day's 24 hourly mean speeds at the target site, continuing from the hour before.
    /// </summary>
    /// <param name="day">The generated day. Its mean speeds set the scale and the within-day shape.</param>
    /// <param name="dayStart">When the day begins, which places the diurnal pattern in UTC.</param>
    /// <param name="random">The hourly stream. Always advanced by 24 draws, whatever the day.</param>
    /// <param name="speeds">Twenty-four slots, overwritten.</param>
    public void FillDay(SyntheticWindDay day, DateTimeOffset dayStart, Random random, Span<double> speeds)
    {
        ArgumentNullException.ThrowIfNull(random);

        if (speeds.Length != HourlyWindModel.HoursPerDay)
            throw new ArgumentException("A day needs 24 slots.", nameof(speeds));

        double rho = _model.Persistence;
        double innovation = Math.Sqrt(1.0 - rho * rho);

        int month = day.Date.Month;
        double a = _model.DiurnalWeight(month);
        double b = Math.Sqrt(1.0 - a * a);
        int firstUtcHour = dayStart.UtcDateTime.Hour;

        for (int h = 0; h < HourlyWindModel.HoursPerDay; h++)
        {
            double shock = Gaussian.Sample(random);
            _latent = _started ? rho * _latent + innovation * shock : shock;
            _started = true;
            _latentDay[h] = _latent;
        }

        double mean = day.MeanSpeed;

        // The latent is standardised within the day before the pattern joins it: a persistent
        // latent varies far less inside one day than across many, and mixing it raw would hand
        // the pattern a larger share of the day than its weight says. Standardised, the weight
        // is exactly the share the fit measured.
        bool varies = HourlyWindModel.Standardise(_latentDay);

        for (int h = 0; h < HourlyWindModel.HoursPerDay; h++)
        {
            int utcHour = (firstUtcHour + h) % HourlyWindModel.HoursPerDay;
            _latentDay[h] = a * _model.DiurnalPattern(month, utcHour) + b * _latentDay[h];
        }

        // A calm or undefined day has no spread to draw; every hour is the day.
        if (!(mean > 0.0) || !varies || !HourlyWindModel.Standardise(_latentDay))
        {
            speeds.Fill(mean);
            return;
        }

        var distribution = _shape.DistributionFor(day.MeanSpeedAtReference, mean);
        double sum = 0.0;

        for (int h = 0; h < HourlyWindModel.HoursPerDay; h++)
        {
            speeds[h] = distribution.Quantile(Gaussian.Cdf(_latentDay[h]));
            sum += speeds[h];
        }

        // Twenty-four quantiles average close to the distribution's mean but not onto it; the
        // day's mean is the fixed point, so it is imposed.
        double scale = mean / (sum / HourlyWindModel.HoursPerDay);
        for (int h = 0; h < HourlyWindModel.HoursPerDay; h++)
            speeds[h] *= scale;
    }
}

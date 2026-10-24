using System.Globalization;
using System.Text;

namespace OpenBP.Application.Overview;

/// <summary>
/// Текстовий формат Prometheus (exposition format 0.0.4) для <c>/metrics</c> Api і воркера (F6-07): <c># HELP</c>,
/// <c># TYPE</c> і рядки <c>name{label="value"} число</c>. Значення міток екрануються.
/// </summary>
public sealed class PrometheusWriter
{
    /// <summary>Content-Type відповіді.</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    private readonly StringBuilder _text = new();

    /// <summary>Заголовок метрики.</summary>
    /// <param name="name">Назва (<c>openbp_…</c>).</param>
    /// <param name="type"><c>counter</c>, <c>gauge</c> або <c>histogram</c>.</param>
    /// <param name="help">Опис.</param>
    public PrometheusWriter Metric(string name, string type, string help)
    {
        _text.Append("# HELP ").Append(name).Append(' ').Append(help.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)).Append('\n');
        _text.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        return this;
    }

    /// <summary>Значення з мітками.</summary>
    /// <param name="name">Назва рядка (для гістограми — з суфіксом <c>_bucket</c>, <c>_sum</c>, <c>_count</c>).</param>
    /// <param name="value">Значення.</param>
    /// <param name="labels">Пари «мітка, значення».</param>
    public PrometheusWriter Sample(string name, double value, params (string Name, string Value)[] labels)
    {
        _text.Append(name);
        if (labels.Length > 0)
        {
            _text.Append('{');
            for (var i = 0; i < labels.Length; i++)
            {
                if (i > 0)
                {
                    _text.Append(',');
                }

                _text.Append(labels[i].Name).Append("=\"").Append(Escape(labels[i].Value)).Append('"');
            }

            _text.Append('}');
        }

        _text.Append(' ').Append(Format(value)).Append('\n');
        return this;
    }

    /// <summary>
    /// Гістограма з кошиків тривалості <see cref="ExecutionStatsBuckets"/> (кількості не накопичені): <c>_bucket</c> з
    /// межами в секундах, <c>+Inf</c>, <c>_sum</c> (с) і <c>_count</c>.
    /// </summary>
    /// <param name="name">Базова назва.</param>
    /// <param name="buckets">Кількості в кошиках.</param>
    /// <param name="sumMs">Сума тривалостей, мс.</param>
    public PrometheusWriter DurationHistogram(string name, IReadOnlyList<long> buckets, long sumMs)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        long cumulative = 0;
        for (var i = 0; i < ExecutionStatsBuckets.BoundsMs.Count; i++)
        {
            cumulative += i < buckets.Count ? buckets[i] : 0;
            Sample(name + "_bucket", cumulative, ("le", Format(ExecutionStatsBuckets.BoundsMs[i] / 1000d)));
        }

        cumulative += buckets.Count > ExecutionStatsBuckets.BoundsMs.Count ? buckets[ExecutionStatsBuckets.BoundsMs.Count] : 0;
        Sample(name + "_bucket", cumulative, ("le", "+Inf"));
        Sample(name + "_sum", sumMs / 1000d);
        return Sample(name + "_count", cumulative);
    }

    /// <inheritdoc />
    public override string ToString() => _text.ToString();

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}

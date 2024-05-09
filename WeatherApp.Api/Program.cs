using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddOpenTelemetry(x =>
{
	x.IncludeScopes = true;
	x.IncludeFormattedMessage = true;
});

// Add services to the container.
builder.Services.AddOpenTelemetry()
	.WithMetrics(x =>
	{
		x.AddRuntimeInstrumentation()
			.AddMeter(
				"Microsoft.AspNetCore.Hosting",
				"Microsoft.AspNetCore.Server.Kestrel",
				"System.Net.Http",
				"WeatherApp.Api"
			);
	})
	.WithTracing(x =>
	{
		if (builder.Environment.IsDevelopment())
		{
			x.SetSampler<AlwaysOnSampler>();
		}

		x.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("WeatherApp.Api"))
			.AddAspNetCoreInstrumentation()
			.AddGrpcClientInstrumentation()
			.AddHttpClientInstrumentation();
	});

builder.Services.Configure<OpenTelemetryLoggerOptions>(logging => logging.AddOtlpExporter());
builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddOtlpExporter());
builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddOtlpExporter());

builder.Services.AddHealthChecks()
	.AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

builder.Services.ConfigureHttpClientDefaults(http =>
{
	http.AddStandardResilienceHandler();
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddMetrics();
builder.Services.AddSingleton<WeatherMetrics>();

var app = builder.Build();

app.MapHealthChecks("/health");

app.MapHealthChecks("/alive", new HealthCheckOptions
{
	Predicate = r => r.Tags.Contains("live")
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
	app.UseSwagger();
	app.UseSwaggerUI();
}

app.UseHttpsRedirection();

var summaries = new[]
{
	"Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", async (WeatherMetrics weatherMetrics) =>
	{
		using var _ = weatherMetrics.MeasureRequestDuration();
		await Task.Delay(Random.Shared.Next(5, 100));

		var forecast = Enumerable.Range(1, 5).Select(index =>
				new WeatherForecast
				(
					DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
					Random.Shared.Next(-20, 55),
					summaries[Random.Shared.Next(summaries.Length)]
				))
			.ToArray();
		
		weatherMetrics.IncreaseWeatherRequestCount();
		
		return forecast;
	})
.WithName("GetWeatherForecast")
.WithOpenApi();

app.Run();

public class WeatherMetrics
{
	private const string MeterName = "WeatherApp.Api";

	private readonly Counter<long> _weatherRequestCounter;
	private readonly Histogram<double> _weatherRequestDuration;

	public WeatherMetrics(IMeterFactory meterFactory)
	{
		var meter = meterFactory.Create(MeterName);
		_weatherRequestCounter = meter.CreateCounter<long>(
			"weatherapp.api.weather_requests.count");

		_weatherRequestDuration = meter.CreateHistogram<double>(
			"weatherapp.api.weather_requests.duration", 
			"ms");
	}

	public void IncreaseWeatherRequestCount()
	{
		_weatherRequestCounter.Add(1);
	}

	public TrackedRequestDuration MeasureRequestDuration()
	{
		return new TrackedRequestDuration(_weatherRequestDuration);
	}
}

public class TrackedRequestDuration(Histogram<double> histogram) : IDisposable
{
	private readonly long _requestStartTime = TimeProvider.System.GetTimestamp();
	private readonly Histogram<double> _histogram = histogram;

	public void Dispose()
	{
		var elapsed = TimeProvider.System.GetElapsedTime(_requestStartTime);
		_histogram.Record(elapsed.TotalMilliseconds);
	}
}

internal record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
	public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

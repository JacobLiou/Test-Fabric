namespace TestFabric.Contracts;

// ---------- OSA (OpticalSpectrumAnalysis) ----------

public record OsaConfigureRequest(double StartNm, double StopNm, double ResolutionNm, string? TraceId = null);

public record OsaConfigureResult(string ResourceId, string AdapterVersion, DateTimeOffset Timestamp, string Unit = "nm");

public record OsaSweepRequest(string? TraceId = null);

public record OsaSweepResult(
    string ResourceId,
    string AdapterVersion,
    string SweepId,
    DateTimeOffset Timestamp,
    int Points,
    bool Processed = false);

public record OsaTrace(
    string ResourceId,
    string AdapterVersion,
    string SweepId,
    DateTimeOffset Timestamp,
    double StartNm,
    double StopNm,
    string Unit,
    double[] ValuesDbm);

public record OsaPeak(string ResourceId, string AdapterVersion, string SweepId, DateTimeOffset Timestamp,
    double PeakWavelengthNm, double PeakPowerDbm, string Unit = "dBm");

// ---------- Power Meter (OpticalPowerMeasurement) ----------

public record PmConfigureRequest(int Channel, string Unit = "dBm");

public record PmConfigureResult(string ResourceId, string AdapterVersion, DateTimeOffset Timestamp);

public record PmMeasureRequest(int Channel, double? WavelengthNm = null, int AveragingCount = 1);

public record PmPowerResult(string ResourceId, string AdapterVersion, DateTimeOffset Timestamp,
    int Channel, double PowerDbm, string Unit = "dBm", bool Processed = false);

public record PmZeroResult(string ResourceId, string AdapterVersion, DateTimeOffset Timestamp, int Channel);

public record CommandAck(string ResourceId, string AdapterVersion, DateTimeOffset Timestamp, string Command);

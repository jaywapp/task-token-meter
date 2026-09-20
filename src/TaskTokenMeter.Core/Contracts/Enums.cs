namespace TaskTokenMeter.Core.Contracts;

public enum StorageMode { Global, Workspace }
public enum InteractionMode { Interactive, NonInteractive, Json, Hook }
public enum ProviderKind { Claude, Codex }
public enum MeasurementQuality { Observed, Provisional, Partial, Invalid, Unsupported }

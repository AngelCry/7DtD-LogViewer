namespace LogViewer.Core;

public enum PollKind { NoChange, Grown, Reset }

public readonly record struct PollResult(PollKind Kind, int OldCount, int NewCount);

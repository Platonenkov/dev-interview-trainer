namespace Trainer.Domain.Levels;

/// <summary>Seniority level of a question. Numeric values are ordered and persisted, so never renumber them.</summary>
public enum Level
{
    Junior = 1,
    Middle = 2,
    Senior = 3,
    Lead = 4,
    Architect = 5,
}

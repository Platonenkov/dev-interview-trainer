namespace Trainer.Application.Users;

/// <summary>The identity claims returned by Telegram at sign-in.</summary>
public sealed record TelegramProfile(long TelegramUserId, string? Username, string? DisplayName);

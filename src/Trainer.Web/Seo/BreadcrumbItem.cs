namespace Trainer.Web.Seo;

/// <summary>One step of a breadcrumb trail; <paramref name="PathAndQuery"/> is site-relative and made absolute on output.</summary>
public sealed record BreadcrumbItem(string Name, string PathAndQuery);

using Foodplanner.Domain.Localization;

namespace Foodplanner.Api.Localization;

/// <summary>
/// The language of a request (T-039): negotiated from <c>Accept-Language</c> (nb/no/nn give nb, en gives en, anything
/// else or nothing gives en). Declare it as an endpoint parameter; minimal APIs bind it with <see cref="BindAsync"/>,
/// so it is not part of the OpenAPI parameters. The generated web client sends the header from the app's active locale.
/// </summary>
public readonly record struct RequestLocale(string Code)
{
    public static RequestLocale From(HttpContext context) =>
        new(Locales.Negotiate(context.Request.Headers.AcceptLanguage.ToString()));

    public static ValueTask<RequestLocale> BindAsync(HttpContext context) => ValueTask.FromResult(From(context));

    public override string ToString() => Code;
}

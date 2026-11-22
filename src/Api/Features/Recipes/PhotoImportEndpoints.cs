using System.Diagnostics;
using System.Globalization;
using Foodplanner.Api.Authorization;
using Foodplanner.Api.Localization;
using Foodplanner.Api.Problems;
using Foodplanner.Api.Security;
using Foodplanner.Api.Tenancy;
using Foodplanner.Domain.Imports;
using Foodplanner.Domain.Recipes;
using Foodplanner.Domain.Users;
using Foodplanner.Infrastructure.Images;
using Foodplanner.Infrastructure.Import.Ai;
using Foodplanner.Infrastructure.Persistence;
using Foodplanner.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Foodplanner.Api.Features.Recipes;

/// <summary>The multipart body of a photo import (documentation of the contract; the endpoint reads the parts itself).</summary>
public sealed record PhotoImportUpload(IFormFile[] Photos, string? SourceName);

/// <summary><c>Enabled</c> false means the feature is off (or has no key): the client hides it. <c>RemainingToday</c> counts photo imports left for the household in the current UTC day.</summary>
public sealed record PhotoUsageStatusResponse(bool Enabled, int DailyLimit, int RemainingToday);

/// <summary>
/// Recipe from a photo (T-118). One to four photos of ONE recipe are read by Claude into an import draft in the existing import
/// flow (review, edit, save). Adult or Owner (and a full session for the call itself, T-105). The order of a call: feature on,
/// body validated, draft limit, concurrency slot, daily and monthly reservation, the call, the call log, the draft. A reservation
/// is consumed when the call is sent, whether or not it succeeds. The photos live only in request memory; only the cropped dish
/// picture is stored, as a temporary blob (Q-035) until the import is saved or discarded. See docs/domain.md "Recipe from a photo".
/// </summary>
public static class PhotoImportEndpoints
{
    public const int MaxBodyBytes = 44 * 1024 * 1024;
    public const int MaxSourceNameLength = Recipe.MaxSourceNameLength;

    public static IEndpointRouteBuilder MapPhotoImportEndpoints(this IEndpointRouteBuilder api)
    {
        var imports = api.MapHouseholdScopedGroup().MapGroup("/imports").RequireAuthorization(Policies.HouseholdAdult);

        // Multipart: the antiforgery token is validated by the /api root group (header), so the framework's form check is off.
        imports.MapPost("/photo", CreatePhotoImport)
            .RequireAuthorization(Policies.FullSession)
            .RequireRateLimiting(RateLimitPolicies.PhotoImport)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .Accepts<PhotoImportUpload>("multipart/form-data")
            .WithName("CreatePhotoRecipeImport")
            .Produces<RecipeImportResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        imports.MapGet("/photo-usage", GetPhotoUsage)
            .WithName("GetPhotoImportUsage")
            .Produces<PhotoUsageStatusResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        imports.MapGet("/{importId:guid}/picture", GetPicture)
            .WithName("GetRecipeImportPicture")
            .Produces(StatusCodes.Status200OK, typeof(byte[]), RecipeImageEndpoints.ContentType)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    private static async Task<IResult> GetPhotoUsage(
        IOptions<AiImportOptions> shared, IOptions<AiPhotoImportOptions> options, IAiUsageLimiter limiter, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.IsActive(shared.Value))
        {
            return Results.Ok(new PhotoUsageStatusResponse(false, settings.DailyPerHousehold, 0));
        }

        return Results.Ok(new PhotoUsageStatusResponse(true, settings.DailyPerHousehold, await limiter.PhotoRemainingTodayAsync(cancellationToken)));
    }

    private static async Task<IResult> GetPicture(
        Guid householdId, Guid importId, HttpContext http, AppDbContext db, IImageStore images, CancellationToken cancellationToken)
    {
        var import = await RecipeImportEndpoints.FindAsync(db, importId, cancellationToken);
        if (import is null || import.Status != ImportStatus.Draft || import.DraftJson is null
            || !ImportDraftJson.Deserialize(import.DraftJson).HasSuggestedImage)
        {
            return RecipeImportEndpoints.NotFound();
        }

        var stream = await images.OpenReadAsync(ImportPictureBlobNames.Picture(import.HouseholdId, import.Id), cancellationToken);
        if (stream is null)
        {
            return RecipeImportEndpoints.NotFound();
        }

        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Stream(stream, RecipeImageEndpoints.ContentType);
    }

    private static async Task<IResult> CreatePhotoImport(
        Guid householdId, HttpRequest http, RequestLocale locale, ICurrentUser user, AppDbContext db,
        IOptions<AiImportOptions> shared, IOptions<AiPhotoImportOptions> photoOptions, IOptions<ImageOptions> imageOptions,
        IAiUsageLimiter limiter, IRecipePhotoReader reader, PhotoImportGate gate, IImageStore images, TimeProvider time, ILogger<RecipeImport> logger,
        CancellationToken cancellationToken)
    {
        var settings = photoOptions.Value;
        if (!settings.IsActive(shared.Value))
        {
            return AppProblems.Problem(
                StatusCodes.Status404NotFound, AppProblems.PhotoImportDisabled, "Photo import is off", "Reading a recipe from photos is not available here.");
        }

        if (user.UserId is not { } userId)
        {
            return AppProblems.Problem(StatusCodes.Status403Forbidden, AppProblems.Forbidden, "Forbidden");
        }

        var form = await ReadFormAsync(http, cancellationToken);
        var errors = new Dictionary<string, string[]>();
        var sourceName = ReadSourceName(form, errors);
        var originals = new List<byte[]>();
        var prepared = new List<PreparedPhoto>();
        await PrepareAllAsync(form?.Files.GetFiles("photos"), settings, imageOptions.Value, originals, prepared, errors, cancellationToken);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (await db.RecipeImports.CountAsync(i => i.Status == ImportStatus.Draft, cancellationToken) >= RecipeImportEndpoints.MaxDrafts)
        {
            return AppProblems.Problem(
                StatusCodes.Status409Conflict,
                AppProblems.ImportLimit,
                "Too many imports waiting",
                $"There are already {RecipeImportEndpoints.MaxDrafts} imports waiting for review. Save or discard some first.");
        }

        using var slot = gate.TryEnter();
        if (slot is null)
        {
            return AppProblems.Problem(
                StatusCodes.Status503ServiceUnavailable, AppProblems.PhotoImportBusy, "Photo import is busy",
                "Too many photo imports are running right now. Try again in a moment.");
        }

        switch (await limiter.TryReservePhotoAsync(cancellationToken))
        {
            case AiReservation.HouseholdLimit:
                return LimitReached(http.HttpContext, settings, time);
            case AiReservation.GlobalLimit:
                return AppProblems.Problem(
                    StatusCodes.Status503ServiceUnavailable, AppProblems.PhotoImportCapacity, "Photo import is full this month",
                    "Photo import is full for this month. Paste the recipe text or use a web address instead.");
        }

        var unitIds = await db.Units.AsNoTracking().ToDictionaryAsync(u => u.Code, u => u.Id, cancellationToken);
        var readRequest = new PhotoReadRequest(prepared, sourceName, [.. unitIds.Keys.Order(StringComparer.Ordinal)]);

        var watch = Stopwatch.StartNew();
        PhotoReadResult result;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)));
            try
            {
                result = await reader.ReadAsync(readRequest, timeout.Token);
            }
            catch (PhotoReaderNotReadyException)
            {
                // Nothing was sent: the reservation is given back.
                await limiter.ReleasePhotoAsync(CancellationToken.None);
                logger.LogWarning("Photo import could not start the reader; the reservation was given back.");
                return await ProblemAsync(PhotoReadOutcome.Failed, limiter, settings, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = new PhotoReadResult(PhotoReadOutcome.Timeout, null, null, null, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Photo import reader failed ({Error}).", ex.GetType().Name);
                result = new PhotoReadResult(PhotoReadOutcome.Failed, null, null, null, null);
            }
        }

        watch.Stop();
        if (result.Outcome != PhotoReadOutcome.Read || result.Recipe is null)
        {
            await WriteLogAsync(db, logger, householdId, settings, prepared.Count, result, ToLogOutcome(result.Outcome), watch, null);
            return await ProblemAsync(result.Outcome, limiter, settings, cancellationToken);
        }

        // From here the answer is mapped; whatever goes wrong is a failed import, never a 500 (and the call is still logged).
        Guid? importId = null;
        try
        {
            var matcher = await RecipeImportEndpoints.LoadMatcherAsync(db, locale.Code, cancellationToken);
            var mapped = PhotoRecipeMapper.Map(result.Recipe, sourceName, prepared.Count, matcher, code => unitIds.TryGetValue(code, out var id) ? id : null);
            var draft = mapped.Draft;

            var import = new RecipeImport(ImportSource.Photo, null, mapped.RawText, ImportDraftJson.Serialize(draft), userId);
            db.RecipeImports.Add(import);
            importId = import.Id;

            var stored = await TryStoreCropAsync(images, imageOptions.Value, logger, householdId, import.Id, originals, mapped.Crop, cancellationToken);
            if (stored)
            {
                draft = draft with { HasSuggestedImage = true, UseSuggestedImage = true };
                import.UpdateDraft(ImportDraftJson.Serialize(draft));
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                if (stored)
                {
                    await RecipeImageEndpoints.TryDeleteBlobsAsync(images, logger, ImportPictureBlobNames.Prefix(householdId, import.Id), CancellationToken.None);
                }

                throw;
            }

            await WriteLogAsync(db, logger, householdId, settings, prepared.Count, result, AiCallOutcome.Ok, watch, import.Id);
            var remaining = await limiter.PhotoRemainingTodayAsync(cancellationToken);
            var response = await RecipeImportEndpoints.ToResponseAsync(
                db, import, draft, locale.Code, cancellationToken, new PhotoUsageResponse(remaining, settings.DailyPerHousehold));
            return Results.Created($"/api/households/{householdId}/imports/{import.Id}", response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Photo import could not build the draft ({Error}).", ex.GetType().Name);
            db.ChangeTracker.Clear();
            await WriteLogAsync(db, logger, householdId, settings, prepared.Count, result, AiCallOutcome.Failed, watch, null);
            return await ProblemAsync(PhotoReadOutcome.Failed, limiter, settings, cancellationToken);
        }
    }

    private static async Task<IFormCollection?> ReadFormAsync(HttpRequest http, CancellationToken cancellationToken)
    {
        if (!http.HasFormContentType)
        {
            return null;
        }

        try
        {
            return await http.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (BadHttpRequestException)
        {
            return null;
        }
    }

    private static string? ReadSourceName(IFormCollection? form, Dictionary<string, string[]> errors)
    {
        var raw = form?["sourceName"].ToString();
        var name = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        if (name is { Length: > MaxSourceNameLength } || name?.Any(char.IsControl) == true)
        {
            errors["sourceName"] = [$"The book name can be at most {MaxSourceNameLength} characters, without control characters."];
            return null;
        }

        return name;
    }

    /// <summary>Validates every file by its bytes (the same checks as a recipe picture) and makes the JPEG that is sent. Bytes stay in memory only.</summary>
    private static async Task PrepareAllAsync(
        IReadOnlyList<IFormFile>? files, AiPhotoImportOptions settings, ImageOptions limits, List<byte[]> originals, List<PreparedPhoto> prepared,
        Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        if (files is null || files.Count == 0)
        {
            errors["photos"] = [$"Send 1 to {settings.MaxPhotos} photos in the form part named 'photos'."];
            return;
        }

        if (files.Count > settings.MaxPhotos)
        {
            errors["photos"] = [$"At most {settings.MaxPhotos} photos can be sent at once."];
            return;
        }

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var key = $"photos[{i}]";
            if (file.Length == 0)
            {
                errors[key] = ["The photo is empty."];
                continue;
            }

            if (file.Length > limits.MaxUploadBytes)
            {
                errors[key] = [$"The photo can be at most {limits.MaxUploadBytes / (1024 * 1024)} MB."];
                continue;
            }

            byte[] bytes;
            await using (var stream = file.OpenReadStream())
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                bytes = buffer.ToArray();
            }

            var result = await Task.Run(() => PhotoImagePreparer.Prepare(bytes, limits), cancellationToken);
            if (!result.Succeeded)
            {
                errors[key] = [result.Message!];
                continue;
            }

            originals.Add(bytes);
            prepared.Add(result.Photo!);
        }
    }

    /// <summary>Crops the dish picture from the ORIGINAL photo and keeps it as the temporary blob. Any failure just means no suggested picture.</summary>
    private static async Task<bool> TryStoreCropAsync(
        IImageStore images, ImageOptions limits, ILogger logger, Guid householdId, Guid importId, List<byte[]> originals, PictureCrop? crop,
        CancellationToken cancellationToken)
    {
        if (crop is null || crop.PhotoIndex >= originals.Count)
        {
            return false;
        }

        try
        {
            var intermediate = await Task.Run(() => PhotoImagePreparer.Crop(originals[crop.PhotoIndex], crop), cancellationToken);
            if (intermediate is null)
            {
                return false;
            }

            var processed = await Task.Run(() => RecipeImageProcessor.Process(intermediate, limits), cancellationToken);
            var full = processed.Images?.FirstOrDefault(i => i.Size == Domain.Images.RecipeImageSize.Full);
            if (full is null)
            {
                return false;
            }

            await images.PutAsync(ImportPictureBlobNames.Picture(householdId, importId), full.Content, RecipeImageProcessor.ContentType, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The suggested picture of a photo import could not be stored ({Error}).", ex.GetType().Name);
            await RecipeImageEndpoints.TryDeleteBlobsAsync(images, logger, ImportPictureBlobNames.Prefix(householdId, importId), CancellationToken.None);
            return false;
        }
    }

    private static AiCallOutcome ToLogOutcome(PhotoReadOutcome outcome) => outcome switch
    {
        PhotoReadOutcome.Read => AiCallOutcome.Ok,
        PhotoReadOutcome.NotARecipe => AiCallOutcome.NotARecipe,
        PhotoReadOutcome.Unreadable => AiCallOutcome.Unreadable,
        PhotoReadOutcome.Refused => AiCallOutcome.Refused,
        PhotoReadOutcome.MaxTokens => AiCallOutcome.MaxTokens,
        PhotoReadOutcome.Timeout => AiCallOutcome.Timeout,
        _ => AiCallOutcome.Failed,
    };

    /// <summary>One AiCallLog row and one log line per call. Counts and ids only; a failure to write is logged and never fails the import.</summary>
    private static async Task WriteLogAsync(
        AppDbContext db, ILogger logger, Guid householdId, AiPhotoImportOptions settings, int photoCount, PhotoReadResult result,
        AiCallOutcome outcome, Stopwatch watch, Guid? importId)
    {
        var duration = (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds);
        logger.LogInformation(
            "Photo import call: household {HouseholdId}, model {Model}, tokens {InputTokens}/{OutputTokens}, stop reason {StopReason}, outcome {Outcome}, {DurationMs} ms.",
            householdId, settings.Model, result.InputTokens, result.OutputTokens, result.StopReason, outcome, duration);
        try
        {
            db.AiCallLogs.Add(new AiCallLog(
                AiCallKind.PhotoImport, settings.Model, settings.Effort, RecipePhotoSkill.Version, photoCount, result.InputTokens, result.OutputTokens,
                result.StopReason, outcome, duration, importId));
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The AI call log row could not be written ({Error}).", ex.GetType().Name);
            foreach (var entry in db.ChangeTracker.Entries<AiCallLog>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private static IResult LimitReached(HttpContext http, AiPhotoImportOptions settings, TimeProvider time)
    {
        var now = time.GetUtcNow();
        var tomorrow = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        var seconds = Math.Max(1, (int)Math.Ceiling((tomorrow - now).TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return AppProblems.Problem(
            StatusCodes.Status429TooManyRequests,
            AppProblems.PhotoImportLimitReached,
            "Photo import limit reached",
            "Your household has used today's photo imports. Try again tomorrow, or paste the recipe text or a web address instead.",
            new Dictionary<string, object?> { ["remainingToday"] = 0, ["dailyLimit"] = settings.DailyPerHousehold });
    }

    private static async Task<IResult> ProblemAsync(PhotoReadOutcome outcome, IAiUsageLimiter limiter, AiPhotoImportOptions settings, CancellationToken cancellationToken)
    {
        var remaining = 0;
        try
        {
            remaining = await limiter.PhotoRemainingTodayAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The count is a courtesy; the problem is what matters.
        }

        var extensions = new Dictionary<string, object?> { ["remainingToday"] = remaining, ["dailyLimit"] = settings.DailyPerHousehold };
        const string fallback = " You can paste the recipe text or use a web address instead.";
        return outcome switch
        {
            PhotoReadOutcome.NotARecipe => AppProblems.Problem(
                StatusCodes.Status422UnprocessableEntity, AppProblems.PhotoImportNotARecipe, "This does not look like a recipe",
                "The photos do not look like a recipe." + fallback, extensions),
            PhotoReadOutcome.Unreadable => AppProblems.Problem(
                StatusCodes.Status422UnprocessableEntity, AppProblems.PhotoImportUnreadable, "The photos could not be read",
                "Could not read the photos. Try a sharper, brighter photo of one page, closer to the text." + fallback, extensions),
            PhotoReadOutcome.Refused => AppProblems.Problem(
                StatusCodes.Status422UnprocessableEntity, AppProblems.PhotoImportRefused, "The photos were declined",
                "The reader declined to read these photos." + fallback, extensions),
            PhotoReadOutcome.Timeout => AppProblems.Problem(
                StatusCodes.Status504GatewayTimeout, AppProblems.PhotoImportTimeout, "Reading the photos took too long",
                "Reading the photos took too long. Try again." + fallback, extensions),
            _ => AppProblems.Problem(
                StatusCodes.Status502BadGateway, AppProblems.PhotoImportFailed, "The photos could not be read",
                "Reading the photos failed. Try again." + fallback, extensions),
        };
    }
}

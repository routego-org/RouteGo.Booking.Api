using FluentValidation.Results;

namespace RouteGo.Booking.Api.Features.Bookings.SearchBookings;

[Collection(DatabaseCollection.Name)]
public sealed class SearchBookingsTests
{
    private static readonly DateTimeOffset _confirmedAt = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly DatabaseFixture _database;

    public SearchBookingsTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_EmailInOtherLetterCase_ListsThatContactsBookingsNewestFirstWithEveryDetail()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        Guid confirmed = await SeedAsync(trip, "C-1", "priya@example.com", BookingStatus.Confirmed);
        Guid failed = await SeedAsync(trip, "C-1", "priya@example.com", BookingStatus.Failed, FailureReason.PaymentDeclined);
        await SeedAsync(trip, "C-2", "priya@example.com.au", BookingStatus.Confirmed);
        await SeedAsync(trip, "C-3", "ravi@example.com", BookingStatus.Confirmed);
        var request = new SearchBookingsRequest { Search = new BookingSearch { ContactEmail = "Priya@Example.com" } };

        // Act
        AdminBookingPage page = await CreateHandler().HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(new[] { failed, confirmed }, page.Items.Select(item => item.BookingId).ToArray());
        Assert.Equal(2, page.TotalItems);
        AdminBookingSummary newest = page.Items[0];
        Assert.Equal(BookingStatus.Failed, newest.Status);
        Assert.Equal("Priya Shah", newest.ContactName);
        Assert.Equal("priya@example.com", newest.ContactEmail);
        Assert.Equal("Mumbai", newest.Trip.SourceCityName);
        Assert.Equal(trip.DepartureAt, newest.Trip.DepartureAt);
        Assert.Equal(new[] { "3A", "3B" }, newest.SeatNos);
        Assert.Equal(1600, newest.TotalAmount);
        Assert.Equal("INR", newest.Currency);
        Assert.True(newest.CreatedAt > page.Items[1].CreatedAt);
    }

    [Fact]
    public async Task HandleAsync_TripWithBookingsInEveryStatus_ListsAllOfThemIncludingTheUnpaidOne()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        var otherTrip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        await BookingSeed.SeedTripAsync(_database, otherTrip);
        await SeedAsync(trip, "C-1", "a@example.com", BookingStatus.Confirmed);
        await SeedAsync(trip, "C-2", "b@example.com", BookingStatus.Confirmed);
        await SeedAsync(trip, "C-3", "c@example.com", BookingStatus.PaymentPending);
        await SeedAsync(trip, "C-4", "d@example.com", BookingStatus.Cancelled);
        await SeedAsync(trip, "C-5", "e@example.com", BookingStatus.Expired);
        await SeedAsync(otherTrip, "C-1", "a@example.com", BookingStatus.Confirmed);
        var request = new SearchBookingsRequest { Search = new BookingSearch { TripId = trip.TripId } };

        // Act
        AdminBookingPage page = await CreateHandler().HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(5, page.TotalItems);
        Assert.Equal(5, page.Items.Count);
        Assert.Contains(page.Items, item => item.Status == BookingStatus.PaymentPending);
        Assert.All(page.Items, item => Assert.Equal(trip.TripId, item.Trip.TripId));
    }

    [Fact]
    public async Task HandleAsync_BookingIdGiven_ListsExactlyThatBooking()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        await SeedAsync(trip, "C-1", "priya@example.com", BookingStatus.Confirmed);
        Guid wanted = await SeedAsync(trip, "C-2", "priya@example.com", BookingStatus.Confirmed);
        var request = new SearchBookingsRequest { Search = new BookingSearch { BookingId = wanted } };

        // Act
        AdminBookingPage page = await CreateHandler().HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(wanted, Assert.Single(page.Items).BookingId);
        Assert.Equal(1, page.TotalItems);
    }

    [Fact]
    public async Task HandleAsync_EmailAndTripGiven_ListsOnlyThatContactsBookingsOnThatTrip()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var wantedTrip = new TripSeed();
        var otherTrip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, wantedTrip);
        await BookingSeed.SeedTripAsync(_database, otherTrip);
        Guid wanted = await SeedAsync(wantedTrip, "C-1", "priya@example.com", BookingStatus.Confirmed);
        await SeedAsync(otherTrip, "C-1", "priya@example.com", BookingStatus.Confirmed);
        await SeedAsync(wantedTrip, "C-2", "ravi@example.com", BookingStatus.Confirmed);
        var search = new BookingSearch { ContactEmail = "priya@example.com", TripId = wantedTrip.TripId };

        // Act
        AdminBookingPage page = await CreateHandler().HandleAsync(new SearchBookingsRequest { Search = search }, CancellationToken.None);

        // Assert
        Assert.Equal(wanted, Assert.Single(page.Items).BookingId);
        Assert.Equal(1, page.TotalItems);
    }

    [Fact]
    public async Task HandleAsync_EmailNoBookingHas_ReturnsAnEmptyPageNotAnError()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        await SeedAsync(trip, "C-1", "priya@example.com", BookingStatus.Confirmed);
        var request = new SearchBookingsRequest { Search = new BookingSearch { ContactEmail = "nobody@example.com" } };

        // Act
        AdminBookingPage page = await CreateHandler().HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalItems);
    }

    [Fact]
    public async Task HandleAsync_FortyFiveBookingsOnATripPageTwoOfTwenty_ReturnsBookingsTwentyOneToFortyNewestFirst()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        IReadOnlyList<Guid> seeded = await SeedManyAsync(trip, 45);
        var request = new SearchBookingsRequest { Search = new BookingSearch { TripId = trip.TripId }, Page = 2, PageSize = 20 };

        // Act
        AdminBookingPage page = await CreateHandler().HandleAsync(request, CancellationToken.None);

        // Assert
        Guid[] newestFirst = seeded.Reverse().ToArray();
        Guid[] expected = newestFirst.Skip(20).Take(20).ToArray();
        Assert.Equal(expected, page.Items.Select(item => item.BookingId).ToArray());
        Assert.Equal(45, page.TotalItems);
        Assert.Equal(2, page.Page);
        Assert.Equal(20, page.PageSize);
    }

    [Fact]
    public void Validate_NoBookingIdNoEmailNoTrip_IsRefusedNamingTheSearch()
    {
        // Arrange
        var validator = new SearchBookingsValidator();
        var request = new SearchBookingsRequest { Search = new BookingSearch() };

        // Act
        ValidationResult result = validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal("search", Assert.Single(result.Errors).PropertyName);
    }

    [Fact]
    public void Validate_OnlyABlankEmail_IsRefused()
    {
        // Arrange
        var validator = new SearchBookingsValidator();
        var request = new SearchBookingsRequest { Search = new BookingSearch { ContactEmail = "   " } };

        // Act
        ValidationResult result = validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_AnyOneCriterion_IsAccepted()
    {
        // Arrange
        var validator = new SearchBookingsValidator();
        var byBooking = new SearchBookingsRequest { Search = new BookingSearch { BookingId = Guid.NewGuid() } };
        var byEmail = new SearchBookingsRequest { Search = new BookingSearch { ContactEmail = "priya@example.com" } };
        var byTrip = new SearchBookingsRequest { Search = new BookingSearch { TripId = Guid.NewGuid() } };

        // Act
        ValidationResult forBooking = validator.Validate(byBooking);
        ValidationResult forEmail = validator.Validate(byEmail);
        ValidationResult forTrip = validator.Validate(byTrip);

        // Assert
        Assert.True(forBooking.IsValid);
        Assert.True(forEmail.IsValid);
        Assert.True(forTrip.IsValid);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(null, 201)]
    [InlineData(0, null)]
    public void Validate_PageOrPageSizeOutOfRange_IsRefused(int? page, int? pageSize)
    {
        // Arrange
        var validator = new SearchBookingsValidator();
        var request = new SearchBookingsRequest { Search = new BookingSearch { TripId = Guid.NewGuid() }, Page = page, PageSize = pageSize };

        // Act
        ValidationResult result = validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
    }

    private SearchBookingsHandler CreateHandler()
    {
        return new SearchBookingsHandler(TestWriters.CreateReader(_database));
    }

    // Oldest first: each booking is stamped created when it is inserted, so the last one is the newest.
    private async Task<IReadOnlyList<Guid>> SeedManyAsync(TripSeed trip, int count)
    {
        var seeded = new List<Guid>();
        for (int index = 0; index < count; index++)
        {
            seeded.Add(await SeedAsync(trip, "C-" + index, "bulk@example.com", BookingStatus.Confirmed));
        }

        return seeded;
    }

    private async Task<Guid> SeedAsync(TripSeed trip, string customerId, string email, BookingStatus status, FailureReason? reason = null)
    {
        var booking = new BookingSeedSpec
        {
            TripId = trip.TripId,
            CustomerId = customerId,
            ContactEmail = email,
            Status = status,
            FailureReason = reason,
            ConfirmedAt = _confirmedAt,
        };
        await BookingSeed.SeedBookingAsync(_database, booking);
        return booking.BookingId;
    }
}

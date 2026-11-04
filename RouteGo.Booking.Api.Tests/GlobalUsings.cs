// Slice test files (linked in from {Project}/Features/**/*.Tests.cs) carry no using directives —
// this file holds the test project's shared set. The sibling slice skill EXTENDS it as its
// test helpers need more (e.g. Testcontainers, AngleSharp).
global using Xunit;
global using RouteGo.Booking.Api.Common;             // HttpClients, WireJson
global using System.Net;                             // HttpStatusCode in slice tests
global using RouteGo.Booking.Api.Tests;              // ApiFactory
global using RouteGo.Booking.Api.Tests.Database;     // DatabaseFixture, DatabaseCollection
global using RouteGo.Booking.Api.Tests.Support;      // BookingTestHost, TestAccessTokens, fakes, seeds
global using RouteGo.Booking.Api.Common.Ports;       // IClock and the ports the fakes stand in for
global using RouteGo.Booking.Api.Common.Security;    // Caller
global using RouteGo.Booking.Api.Common.Messaging;   // ReceivedEvent, EventOutcome
global using RouteGo.Booking.Api.Domain;             // BookingStatus, FailureReason, RefundReason

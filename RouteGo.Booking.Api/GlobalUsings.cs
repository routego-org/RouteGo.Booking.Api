// Slice files carry no using directives — this file holds the shared set.
// The sibling slice skill and database setup EXTEND this list beneath the '// FILLED BY' marker below.
global using System.Text.Json;          // JsonSerializer, WireJson.Options
global using Microsoft.AspNetCore.Mvc;
global using RouteGo.Booking.Api.Common;             // HttpClients, WireJson
global using RouteGo.Booking.Api.Common.Ports;       // IClock
global using RouteGo.Booking.Api.Common.Problems;    // ProblemResult, ProblemTypes (catalog projection)
global using RouteGo.Booking.Api.Common.Behaviors;   // UnhandledExceptionHandler, ResponseMetadataMiddleware, ResponseHeadersTransformer
// FILLED BY the sibling slice skill — and by skills-db-postgres (copy-assets.ps1) at database setup:
global using System.Data.Common;   // skills-db-postgres (copy-assets.ps1)
global using Dapper;   // skills-db-postgres (copy-assets.ps1)
global using FluentValidation;                     // slice validators
global using FluentValidation.Results;             // ValidationResult in the controller
global using Microsoft.AspNetCore.Authorization;   // [Authorize] on slice endpoints
global using Amazon.SQS;                           // IAmazonSQS - the event queues
global using Amazon.SimpleNotificationService;     // IAmazonSimpleNotificationService - where booking events go
global using RouteGo.Booking.Api.Common.Messaging; // EventQueueService, IEventWorker, ReceivedEvent - the queue workers
global using RouteGo.Booking.Api.Common.Security;  // Policies, Caller
global using RouteGo.Booking.Api.Common.Events;    // BookingEventRecorder, BookingEventContext
global using RouteGo.Booking.Api.Domain;           // BookingStatus, BookingRules, RefundRules, enums
global using Amazon.SimpleSystemsManagement;      // IAmazonSimpleSystemsManagement - the business settings

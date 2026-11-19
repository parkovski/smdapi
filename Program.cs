using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using SQLite;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddCors(options => {
    options.AddPolicy(
        "AllowLocalhostAndSmd",
        builder => {
            builder
                .WithOrigins(
                    "http://localhost:5173",
                    "https://www.shadowmtndigital.com"
                )
                .WithMethods("GET", "POST");
        }
    );
});

var app = builder.Build();

app.UseCors("AllowLocalhostAndSmd");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

string[] Times = ["09:00", "11:00", "14:00", "16:00"];
string[] FridayTimes = ["09:00", "12:00"];

SQLiteAsyncConnection db = new("./appointments.db");

app.MapGet("/appointment/ranges", async () => {
    DateOnly today = DateOnly.FromDateTime(DateTime.Now);
    var rows = await db.QueryAsync<DateTimeResult>(
        """SELECT "date", "time" FROM appointments WHERE date("date") >= date(?) """
        + """ORDER BY "date" ASC""",
        string.Format("{0}-{1:D2}-{2:D2}", today.Year, today.Month, today.Day)
    );

    if (rows.Count == 0) {
        return Results.Text("[]", contentType: "application/json");
    }

    var ranges = new List<DateRange>();
    DateOnly start = DateOnly.Parse(rows[0].Date!);
    DateOnly end = start;
    for (int i = 1; i < rows.Count; ++i) {
        var date = DateOnly.Parse(rows[i].Date!);
        if (date.DayOfYear == end.DayOfYear) {
            // Do nothing
        } else if (date.DayOfYear == end.DayOfYear + 1) {
            end = date;
        } else if (date.Year == end.Year + 1
            && date.DayOfYear == 1 && end.Month == 12 && end.Day == 31) {
            end = date;
        } else {
            ranges.Add(new() { Start = start, End = end });
            start = date;
            end = date;
        }
    }
    if (ranges.Count == 0 || end > ranges[^1].End) {
        ranges.Add(new() { Start = start, End = end });
    }

    StringBuilder sb = new();
    bool first = true;
    sb.Append("[");
    foreach (var range in ranges) {
        if (!first) {
            sb.Append(", ");
        }
        first = false;
        if (range.Start == range.End) {
            sb.Append(string.Format(
                "\"{0}-{1:D2}-{2:D2}\"",
                range.Start.Year, range.Start.Month, range.Start.Day
            ));
        } else {
            sb.Append(string.Format(
                "[\"{0}-{1:D2}-{2:D2}\", \"{3}-{4:D2}-{5:D2}\"]",
                range.Start.Year, range.Start.Month, range.Start.Day,
                range.End.Year, range.End.Month, range.End.Day
            ));
        }
    }
    sb.Append("]");

    return Results.Text(sb.ToString(), contentType: "application/json");
})
.WithName("AppointmentRanges");

app.MapGet("/appointment/booked", async () => {
    DateOnly today = DateOnly.FromDateTime(DateTime.Now);
    var rows = await db.QueryAsync<Appointment>(
        """SELECT "date", "time", email, message FROM appointments """
        + """WHERE date("date") >= date(?) AND email IS NOT NULL """
        + """ORDER BY "date" ASC""",
        string.Format("{0}-{1:D2}-{2:D2}", today.Year, today.Month, today.Day)
    );

    if (rows.Count == 0) {
        return Results.NotFound();
    }

    return Results.Json(rows);
});

app.MapGet("/appointment/{date}", async (string date) => {
    var rows = await db.QueryAsync<TimeResult>(
        """SELECT "time" FROM appointments WHERE "date" = ? AND email is null""",
        date
    );

    StringBuilder sb = new();
    sb.Append("[");
    bool first = true;
    foreach (var time in rows) {
        if (!first) {
            sb.Append(", ");
        }
        first = false;
        sb.Append($"\"{time.Time}\"");
    }
    sb.Append("]");

    return Results.Text(sb.ToString(), contentType: "application/json");
})
.WithName("OpenAppointmentsForDate");

app.MapGet("/appointment/{date}/{time}", async (string date, string time) => {
    var rows = await db.QueryAsync<Appointment>(
        """SELECT "date", "time", email, message FROM appointments """
        + """WHERE "date" = ? AND "time" = ?""",
        date, time
    );

    if (rows.Count == 0) {
        return Results.NotFound();
    }

    return Results.Json(rows[0]);
})
.WithName("GetAppointment");

app.MapPost("/appointment", async ([FromForm] string date,
                                   [FromForm] string time,
                                   [FromForm] string email,
                                   [FromForm] string message,
                                   [FromQuery] string r) => {
    var emailRows = await db.QueryAsync<EmailResult>(
        """SELECT email FROM appointments WHERE "date" = ? AND "time" = ?""",
        date, time
    );
    if (emailRows.Count == 0) {
        return Results.NotFound("no appointment");
    }
    if (emailRows[0].Email != null) {
        return Results.BadRequest("appointment already exists");
    }

    int rows = await db.ExecuteAsync(
        "UPDATE appointments SET email = ?, message = ? "
        + """WHERE "date" = ? AND "time" = ?""",
        email, message,
        date, time
    );

    if (rows == 1) {
        return Results.Redirect(r);
    } else if (rows == 0) {
        return Results.InternalServerError("update failed");
    } else {
        return Results.InternalServerError("too many rows");
    }
})
.DisableAntiforgery()
.WithName("ScheduleAppointment");

app.MapPost("/appointment/add/{date}", async (string date) => {
    DateOnly dateDate = DateOnly.Parse(date);
    string[] timesToInsert = Times;
    if (dateDate.DayOfWeek == DayOfWeek.Friday) {
        timesToInsert = FridayTimes;
    }

    int totalRows = 0;
    foreach (var time in timesToInsert) {
        var rows = await db.ExecuteAsync(
            """INSERT OR IGNORE INTO appointments ("date", "time") VALUES (?, ?)""",
            date, time
        );
        if (rows > 1) {
            return Results.InternalServerError("too many rows");
        }
        totalRows += rows;
    }
    return Results.Ok($"{totalRows} rows inserted");
})
.WithName("AddAppointmentDate");

app.MapPost("/appointment/add/{date}/{time}", async (string date, string time) => {
    var rows = await db.ExecuteAsync(
        """INSERT OR IGNORE INTO appointments ("date", "time") VALUES (?, ?)""",
        date, time
    );
    if (rows == 1) {
        return Results.Ok();
    } else if (rows == 0) {
        return Results.BadRequest("nothing was inserted");
    } else {
        return Results.InternalServerError("too many rows");
    }
})
.WithName("AddOneAppointment");

app.MapPost("/appointment/addrange/{from}/{to}", async (string from, string to) => {
    DateOnly dateFrom = DateOnly.Parse(from);
    DateOnly dateTo = DateOnly.Parse(to);
    DateOnly date = dateFrom;
    int totalRows = 0;
    while (date <= dateTo) {
        string[] timesToInsert = Times;
        if (date.DayOfWeek == DayOfWeek.Friday) {
            timesToInsert = FridayTimes;
        }
        foreach (var time in timesToInsert) {
            var rows = await db.ExecuteAsync(
                """INSERT OR IGNORE INTO appointments ("date", "time") VALUES (?, ?)""",
                $"{date.Year}-{date.Month:D2}-{date.Day:D2}", time
            );
            if (rows > 1) {
                return Results.InternalServerError("too many rows");
            }
            totalRows += rows;
        }
        date = date.AddDays(1);
    }
    return Results.Ok($"{totalRows} rows inserted");
})
.WithName("AddAppointmentRange");

app.Run();

public class EmailResult {
    [Column("email")]
    public string? Email { get; set; }
}

public class DateTimeResult {
    [Column("date")]
    public string? Date { get; set; }

    [Column("time")]
    public string? Time { get; set; }
}

public class TimeResult {
    [Column("time")]
    public string? Time { get; set; }
}

[Table("appointments")]
public class Appointment {
    [Column("date")]
    [NotNull]
    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [Column("time")]
    [NotNull]
    [JsonPropertyName("time")]
    public string? Time { get; set; }

    [Column("email")]
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [Column("message")]
    [JsonPropertyName("message")]
    public string? Message { get; set; }
};

public struct DateRange {
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
}

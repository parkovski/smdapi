CREATE TABLE appointments(
  "date" date NOT NULL,
  "time" time NOT NULL,
  "name" text,
  "email" text,
  "message" text,
  PRIMARY KEY("date", "time")
);

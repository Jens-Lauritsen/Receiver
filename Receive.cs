using System;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using MySql.Data.MySqlClient;
using System.Text.Json;
using Receive.Models;

class Program
{
    // Vi har oprettet en MySQL database, og angiver connectionString her
    private static readonly string connectionString = "Server=localhost;Port=3307;Database=students_db;Uid=root;Pwd=root;";

    //Vi har hardcoded EventId til 1 for nu
    private static readonly int EventId = 1;

    //Vi angiver kønavn, connection factory og connection og channel til RabbitMQ
    private static readonly string StudentDataQueueName = "student_checkin_queue";
    private static readonly ConnectionFactory factory = new ConnectionFactory { HostName = "localhost" };
    private static IConnection rmqConnection;
    private static IModel rmqChannel;

    static void Main(string[] args)
    {
        // Vi initialiserer vores RabbitMQ, for at sende den 'enrichede' data videre til vores frontend
        rmqConnection = factory.CreateConnection();
        rmqChannel = rmqConnection.CreateModel();
        rmqChannel.QueueDeclare(queue: StudentDataQueueName,
                                durable: false,
                                exclusive: false,
                                autoDelete: false,
                                arguments: null);

        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();

        //Vi tager fat i vores kø fra vores sender, for at modtage Card UID
        string queueName = "send-card-uid";

        channel.QueueDeclare(queue: queueName,
                             durable: false,
                             exclusive: false,
                             autoDelete: false,
                             arguments: null);

        Console.WriteLine(" [*] Waiting for messages.");

        var consumer = new EventingBasicConsumer(channel);
        consumer.Received += (model, ea) =>
        {
            //Vi modtager bodyen fra message queue, og konverterer den til en string
            var body = ea.Body.ToArray();
            var cardUid = Encoding.UTF8.GetString(body);
            Console.WriteLine($" [x] Received Card UID: {cardUid}");

            // Vi kalder ProcessCardUID metoden, som håndterer logik for at tjekke en student ind.
            ProcessCardUid(cardUid);
        };
        channel.BasicConsume(queue: queueName,
                             autoAck: true,
                             consumer: consumer);

        Console.WriteLine(" Press [enter] to exit.");
        Console.ReadLine();

        rmqChannel.Close();
        rmqConnection.Close();
    }

    //Beklager vi ikke lige helt overholder SOLID principperne her, Morten! Vi skal nok refaktorere det, senere :)
    private static void ProcessCardUid(string cardUid)
    {
        using var connection = new MySqlConnection(connectionString);
        connection.Open();

        try
        {
            // Vi opretter en start på et StudentData objekt, som vi løbende opdaterer, baseret på forskellige conditions.
            var studentData = new StudentData
            {
                CardUid = cardUid,
                EventId = EventId
            };

            // Vi tjekker vores database, om der findes en student med det givne Card UID
            string selectStudentQuery = "SELECT student_id, first_name, last_name, team_id FROM students WHERE card_uid = @cardUID;";
            using var selectStudentCmd = new MySqlCommand(selectStudentQuery, connection);
            selectStudentCmd.Parameters.AddWithValue("@cardUID", cardUid);

            using var reader = selectStudentCmd.ExecuteReader();

            //Hvis der ikke findes en student med Card UID, skriver vi det til konsollen, og angiver statusen for student til not found.
            if (!reader.Read())
            {
                Console.WriteLine($"Error: No student found with Card UID '{cardUid}'.");

                studentData.Status = CheckInStatus.StudentNotFound;
                SendStudentDataToQueue(studentData);
                return;
            }

            // Hvis vi finder en student, henter vi data fra databasen, og opdaterer vores studentData objekt, så vi kan bruge det i vores frontend.
            int studentId = reader.GetInt32("student_id");
            string firstName = reader.GetString("first_name");
            string lastName = reader.GetString("last_name");
            int teamId = reader.GetInt32("team_id");

            // Før vi henter team navnet, lukker vi readeren, da vi ikke kan have flere åbne readers på samme tid i MySQL
            reader.Close();

            // Vi sætter de fundne værdier ind i det tidligere oprettede studentData objekt
            studentData.TeamName = GetTeamName(connection, teamId);
            studentData.StudentId = studentId;
            studentData.FirstName = firstName;
            studentData.LastName = lastName;

            // Hvis student allerede er checked in, skriver vi det til konsollen, og angiver statusen for student til already checked in.
            string selectEventStudentQuery = "SELECT is_checked_in FROM event_student WHERE student_id = @studentId AND event_id = @eventId;";
            using var selectEventStudentCmd = new MySqlCommand(selectEventStudentQuery, connection);
            selectEventStudentCmd.Parameters.AddWithValue("@studentId", studentId);
            selectEventStudentCmd.Parameters.AddWithValue("@eventId", EventId);

            object isCheckedInResult = selectEventStudentCmd.ExecuteScalar();

            bool isCheckedIn = false;

            //Hvis man kan finde en tilhørende student med det korrekte student_id i event_student tabellen.
            if (isCheckedInResult != null)
            {
                isCheckedIn = Convert.ToBoolean(isCheckedInResult);

                //Hvis is_checked_in er true, gør vi ikke noget i databasen, men sætter check in status til already checked in.
                if (isCheckedIn)
                {
                    // Student is already checked in
                    Console.WriteLine($"Student {firstName} {lastName} is already checked in for event {EventId}.");

                    studentData.IsCheckedIn = true;
                    studentData.Status = CheckInStatus.AlreadyCheckedIn;
                }
                //Hvis is_checked_in er false, opdaterer vi is_checked_in til true i event_student tabellen, og sætter check in status til checked in.
                else
                {
                    string updateEventStudentQuery = "UPDATE event_student SET is_checked_in = true WHERE student_id = @studentId AND event_id = @eventId;";
                    using var updateEventStudentCmd = new MySqlCommand(updateEventStudentQuery, connection);
                    updateEventStudentCmd.Parameters.AddWithValue("@studentId", studentId);
                    updateEventStudentCmd.Parameters.AddWithValue("@eventId", EventId);
                    updateEventStudentCmd.ExecuteNonQuery();

                    Console.WriteLine($"Student {firstName} {lastName} has been checked in to event {EventId}.");

                    studentData.IsCheckedIn = true;
                    studentData.Status = CheckInStatus.CheckedIn;
                }
            }
            //Hvis der ikke findes en relation mellem student og event, i student_event tabellen
            //opretter vi et, og angiver is_checked_in til true, og sætter check in status til checked in.
            else
            {
                string insertEventStudentQuery = "INSERT INTO event_student (student_id, event_id, is_checked_in) VALUES (@studentId, @eventId, true);";
                using var insertEventStudentCmd = new MySqlCommand(insertEventStudentQuery, connection);
                insertEventStudentCmd.Parameters.AddWithValue("@studentId", studentId);
                insertEventStudentCmd.Parameters.AddWithValue("@eventId", EventId);
                insertEventStudentCmd.ExecuteNonQuery();

                Console.WriteLine($"Student {firstName} {lastName} has been checked in to event {EventId}.");

                studentData.IsCheckedIn = true;
                studentData.Status = CheckInStatus.CheckedIn;
            }

            // Til sidst, sender vi studentData objektet til vores queue, så vi kan bruge det i vores frontend.
            SendStudentDataToQueue(studentData);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing Card UID '{cardUid}': {ex.Message}");
        }
    }


    //Metode til at hente team navn, baseret på teamId fra student objektet.
    private static string GetTeamName(MySqlConnection connection, int teamId)
    {
        string teamName = "";

        string selectTeamQuery = "SELECT team_name FROM teams WHERE team_id = @teamId;";
        using var selectTeamCmd = new MySqlCommand(selectTeamQuery, connection);
        selectTeamCmd.Parameters.AddWithValue("@teamId", teamId);

        object result = selectTeamCmd.ExecuteScalar();

        if (result != null)
        {
            teamName = result.ToString();
        }

        return teamName;
    }

    //Metode til at sende den oprettede studentData til vores rabbitMQ queue.
    private static void SendStudentDataToQueue(StudentData studentData)
    {
        try
        {
            string message = JsonSerializer.Serialize(studentData);
            var body = Encoding.UTF8.GetBytes(message);

            rmqChannel.BasicPublish(exchange: "",
                                    routingKey: StudentDataQueueName,
                                    basicProperties: null,
                                    body: body);

            Console.WriteLine($" [x] Sent student data to queue: {message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending student data to queue: {ex.Message}");
        }
    }
}


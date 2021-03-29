using System;
using System.Collections.Generic;
using RabbitMqWrapper;
using AirportLibrary;
using AirportLibrary.DTO;
using System.Threading;
using System.Collections.Concurrent;
using AirportLibrary.Delay;
using System.Threading.Tasks;

namespace RegistrationComponent
{
    class Flight
    {
        public string FlightId { get; set; }
        public FlightStatus Status { get; set; }
        public int PasCount { get; set; } = 0;
        public int BagCount { get; set; } = 0;
        public int StandardFood { get; set; } = 0;
        public int VeganFood { get; set; } = 0;
        public int ChildFood { get; set; } = 0;
    }
    class Registration
    {
        public List<Flight> Flights { get; set; } = new List<Flight>();
        public List<CheckInRequest> PasList { get; set; } = new List<CheckInRequest>();
        public RabbitMqClient MqClient { get; set; } = new RabbitMqClient();
        public PlayDelaySource DelaySource { get; set; } = new PlayDelaySource(1);

        const int MIN_ERR_MS = 10 * 1000; // задержка пассажира от 10 секунд 
        const int MAX_ERR_MS = 10 * 60 * 1000; // до 10 минут игрового времени
        const int REG_TIME_MS = 15 * 1000; // регистрация - 15 секунд игрового времени

        const string timeReg = Component.TimeService + Component.Registration;
        const string scheduleReg = Component.Schedule + Component.Registration;
        const string pasReg = Component.Passenger + Component.Registration;
        const string regPas = Component.Registration + Component.Passenger;
        const string regStorage = Component.Registration + Component.Storage;
        const string regStorageBaggage = Component.Registration + Component.Storage + Subject.Baggage;
        const string regCash = Component.Registration + Component.Cashbox;
        const string cashReg = Component.Cashbox + Component.Registration;
        const string regGrServ = Component.Registration + Component.GroundService;

        public static readonly List<string> queues = new List<string>
        {
            timeReg, scheduleReg, pasReg, regPas, regStorage, regStorageBaggage, regCash, cashReg, regGrServ
        };

        readonly ConcurrentQueue<CheckInRequest> checkInRequests = new ConcurrentQueue<CheckInRequest>();
        readonly AutoResetEvent checkInEvent = new AutoResetEvent(false);

        static void Main(string[] args)
        {
            new Registration().Start();
        }

        public void Start()
        {
            MqClient.DeclareQueues(queues.ToArray());
            MqClient.PurgeQueues(queues.ToArray());

            MqClient.SubscribeTo<NewTimeSpeedFactor>(timeReg, (mes) =>
            {
                DelaySource.TimeFactor = mes.Factor;
            });

            MqClient.SubscribeTo<FlightStatusUpdate>(scheduleReg, (mes) =>
            {
                Console.WriteLine($"Received from Schedule: {mes.FlightId} - {mes.Status}");
                UpdateFlightStatus(mes.FlightId, mes.Status);
            });

            Task.Run(() =>
            {
                while (true)
                {
                    checkInEvent.WaitOne();

                    while (checkInRequests.TryDequeue(out var request))
                    {
                        DelaySource.CreateToken().Sleep(REG_TIME_MS);
                        Registrate(
                            request.PassengerId, 
                            request.FlightId, 
                            request.HasBaggage,
                            request.FoodType
                        );
                    }
                }
            });

            MqClient.SubscribeTo<CheckInRequest>(pasReg, (mes) =>
            {
                Console.WriteLine($"Received from Passenger: {mes.PassengerId}, {mes.FlightId}, {mes.HasBaggage}, {mes.FoodType}");
                checkInRequests.Enqueue(mes);
                checkInEvent.Set();
            });

            // Ответ кассы
            MqClient.SubscribeTo<CheckTicketResponse>(cashReg, (mes) =>
            {
                lock (PasList)
                {
                    var match = PasList.Find(e => (e.PassengerId == mes.PassengerId));
                    if (match != null)
                    {
                        if (mes.HasTicket) // Если билет верный
                        {
                            MqClient.Send(regPas,
                                new CheckInResponse() { PassengerId = mes.PassengerId, Status = CheckInStatus.Registered }
                            );
                            Console.WriteLine($"Sent to Passenger: {mes.PassengerId}, {CheckInStatus.Registered}");
                            Task.Run(() =>
                            {
                                PassToTerminal(match.PassengerId, match.FlightId, match.HasBaggage, match.FoodType);
                            });
                        }
                        else // Если билет неверный
                        {
                            MqClient.Send(regPas,
                                new CheckInResponse() { PassengerId = mes.PassengerId, Status = CheckInStatus.WrongTicket }
                            );
                            Console.WriteLine($"Sent to Passenger: {mes.PassengerId}, {CheckInStatus.WrongTicket}");
                        }

                        PasList.Remove(match);
                    }
                }
            });

            //MqClient.Dispose();
        }

        public void UpdateFlightStatus(string id, FlightStatus status)
        {
            lock (Flights)
            {
                switch (status)
                {
                    case FlightStatus.New:
                        Flights.Add(new Flight() { FlightId = id, Status = status });
                        Console.WriteLine($"Added new flight {id}");
                        break;
                    case FlightStatus.CheckIn:
                        Flights.Find(e => (e.FlightId == id)).Status = status;
                        Console.WriteLine($"Added check-in: {id} - {status}");
                        break;
                    case FlightStatus.Boarding:
                        var boarding = Flights.Find(e => (e.FlightId == id));
                        boarding.Status = status;
                        Console.WriteLine($"Added boarding: {id} - {status}");
                        MqClient.Send(
                            regGrServ,
                            new FlightInfo()
                            {
                                FlightId = id,
                                PassengerCount = boarding.PasCount,
                                BaggageCount = boarding.BagCount,
                                FoodList = new List<Tuple<Food, int>>()
                                {
                                    Tuple.Create(Food.Standard, boarding.StandardFood),
                                    Tuple.Create(Food.Vegan, boarding.VeganFood),
                                    Tuple.Create(Food.Child, boarding.ChildFood),
                                }
                            }
                        );
                        Console.WriteLine($"Sent to Ground Service: {id}, {boarding.PasCount}, {boarding.BagCount}, {boarding.StandardFood}, {boarding.VeganFood}, {boarding.ChildFood}");
                        break;
                    case FlightStatus.Departed:
                        Flights.Find(e => (e.FlightId == id)).Status = status;
                        Console.WriteLine($"Added departed: {id} - {status}");
                        break;
                    default:
                        break;
                }
            }
        }

        public void PassToTerminal(string passengerId, string flightId, bool baggage, Food food)
        {
            var rand = new Random().NextDouble();
            if (rand < 0.2) // Вероятность лагания пассажира - 20%
            {
                var errorTime = new Random().Next(MIN_ERR_MS, MAX_ERR_MS);
                DelaySource.CreateToken().Sleep(errorTime);
            }

            lock (Flights)
            {
                var flight = Flights.Find(e => e.FlightId == flightId);
                var status = flight.Status;

                if (status == FlightStatus.Boarding || status == FlightStatus.Departed)
                {
                    MqClient.Send(
                        regPas,
                        new CheckInResponse() { PassengerId = passengerId, Status = CheckInStatus.LateForTerminal }
                    );
                    Console.WriteLine($"Sent to Passenger: {passengerId}, {CheckInStatus.LateForTerminal}");
                    return;
                }

                // Отправить пассажира в накопитель
                MqClient.Send(regStorage,
                        new PassengerStoragePass() { PassengerId = passengerId, FlightId = flightId });
                flight.PasCount++;
                MqClient.Send(
                        regPas,
                        new CheckInResponse() { PassengerId = passengerId, Status = CheckInStatus.Terminal }
                    );

                if (baggage)
                {
                    // Отправить багаж в накопитель - Накопитель(flightId)
                    MqClient.Send(regStorageBaggage,
                        new BaggageStoragePass() { FlightId = flightId });
                    flight.BagCount++;
                }

                // Добавить еду для рейса
                switch (food)
                {
                    case Food.Standard:
                        flight.StandardFood++;
                        break;
                    case Food.Vegan:
                        flight.VeganFood++;
                        break;
                    case Food.Child:
                        flight.ChildFood++;
                        break;
                    default:
                        break;
                }
            }
        }

        public void Registrate(string passengerId, string flightId, bool hasBaggage, Food foodType)
        {
            Flight flight;
            lock (Flights)
            {
                flight = Flights.Find(e => e.FlightId == flightId);
            }

            if (flight == null)
            {
                MqClient.Send(
                        regPas,
                        new CheckInResponse() { PassengerId = passengerId, Status = CheckInStatus.NoSuchFlight }
                    );
                Console.WriteLine($"Sent to Passenger: {passengerId}, {CheckInStatus.NoSuchFlight}");
                return;
            }

            switch (flight.Status)
            {
                case FlightStatus.New:
                    MqClient.Send(
                        regPas,
                        new CheckInResponse() { PassengerId = passengerId, Status = CheckInStatus.Early }
                    );
                    Console.WriteLine($"Sent to Passenger: {passengerId}, {CheckInStatus.Early}");
                    break;

                case FlightStatus.Boarding:
                case FlightStatus.Delayed:
                case FlightStatus.Departed:
                    MqClient.Send(
                        regPas,
                        new CheckInResponse() { PassengerId = passengerId, Status = CheckInStatus.Late }
                    );
                    Console.WriteLine($"Sent to Passenger: {passengerId}, {CheckInStatus.Late}");
                    break;

                case FlightStatus.CheckIn:
                    lock (PasList)
                    {
                        PasList.Add(
                            new CheckInRequest()
                            {
                                PassengerId = passengerId,
                                FlightId = flightId,
                                HasBaggage = hasBaggage,
                                FoodType = foodType
                            }
                        );
                    }
                    // Отправить запрос кассе на проверку билета
                    MqClient.Send(
                        regCash,
                        new CheckTicketRequest() { PassengerId = passengerId, FlightId = flightId }
                    );
                    Console.WriteLine($"Sent to CashBox: {passengerId}, {flightId}");
                    break;
            }
        }
    }
}

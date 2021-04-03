using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CheckIn.DTO
{
    public class GatePlacement
    {
        public Guid PassengerId { get; set; }
        public Guid FlightId { get; set; }
        public bool HasBaggage { get; set; }
        public bool IsVip { get; set; }
        public int GateNum { get; set; }

        public DateTime Timestamp { get; set; }
    }
}


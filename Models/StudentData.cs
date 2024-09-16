using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Receive.Models
{
    public class StudentData
    {
        public int StudentId { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string TeamName { get; set; }
        public bool IsCheckedIn { get; set; }
        public int EventId { get; set; }
        public string CardUid { get; set; }
        public CheckInStatus Status { get; set; }
    }

    public enum CheckInStatus
    {
        CheckedIn,
        AlreadyCheckedIn,
        StudentNotFound
    }
}

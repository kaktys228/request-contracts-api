namespace APIdIplom.Models
{
    public class Request
    {
        public int RequestID { get; set; }
        public int CustomerID { get; set; }
        public DateTime RequestDate { get; set; }
        public int StatusID { get; set; }
        public decimal TotalAmount { get; set; }
        public string Description { get; set; }

        public int? ContractID { get; set; }
        public List<RequestSignatureInfo> Signatures { get; set; } = new();
        public DateTime? SignedDate { get; set; }
        public string? ContractNumber { get; set; }
        public string? ContractStatusName { get; set; }
        public decimal? ContractAmount { get; set; } // если хочешь сумму

        public int? CreatedByUserId { get; set; }
        public int? CompletedByUserId { get; set; }


        // Вложенные объекты
        public Customer Customer { get; set; }
        public Status Status { get; set; }

    }
    public class RequestSignatureInfo
    {
        public int UserId { get; set; }
        public string FullName { get; set; }
        public string Role { get; set; }
        public DateTime SignedDateTime { get; set; }
    }
}

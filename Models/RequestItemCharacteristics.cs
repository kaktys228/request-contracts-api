namespace APIdIplom.Models
{
    public class RequestItemCharacteristics
    {
        public int RequestItemCharacteristicID { get; set; }
        public string ValueRequest { get; set; }
        public int ProductCharacteristicID { get; set; }

        // Навигационные свойства
        public ProductCharacteristics ProductCharacteristic { get; set; }
    }
}

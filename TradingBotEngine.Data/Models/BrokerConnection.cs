using System.ComponentModel.DataAnnotations;

namespace TradingBotEngine.Data.Models
{
    public class BrokerConnection
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }
        public User User { get; set; } = null!;

        [Required, MaxLength(50)]
        public string BrokerName { get; set; } = string.Empty;

        /// <summary>Encrypted at rest. Never expose this through an API response.</summary>
        [Required]
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>Encrypted at rest. Never expose this through an API response.</summary>
        public string? ApiSecret { get; set; }

        public string? AccountId { get; set; }

        public bool IsActive { get; set; } = true;
        public bool IsConnected { get; set; }

        /// <summary>New connections default to testnet/paper trading.</summary>
        public bool IsTestnet { get; set; } = true;

        /// <summary>Explicit opt-in required before a live order may be sent.</summary>
        public bool IsLiveTradingEnabled { get; set; } = false;


        public bool IsFutures { get; set; }  
        public DateTime? LiveTradingOptInAt { get; set; }

        public DateTime? LastConnectedAt { get; set; }
        public DateTime? LastDisconnectedAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
    }
}

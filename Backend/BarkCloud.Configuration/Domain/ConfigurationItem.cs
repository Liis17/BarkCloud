using BarkCloud.Shared.Identity;

using System.ComponentModel.DataAnnotations;

namespace BarkCloud.Configuration.Domain;

public class ConfigurationItem
{
    [Key]
    public long Id { get; set; }

    public string Section { get; set; }

    public string Key { get; set; }

    public string Value { get; set; }

    public DateTime EditedAt { get; set; }

    public string EditedBy { get; set; }

    public string EditedFrom { get; set; }

    public ServiceId ServiceId { get; set; }
}
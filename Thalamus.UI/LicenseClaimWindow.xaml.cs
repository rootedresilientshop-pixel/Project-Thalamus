using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Thalamus.UI;

/// <summary>
/// LicenseClaimWindow - The "Vending Machine" UI
///
/// Allows users to:
/// 1. Enter a purchase code (from DreamCraft marketplace)
/// 2. Retrieve their hardware ID
/// 3. Call Cloudflare Worker to claim a signed key
/// 4. Save the key to d:/Projects/Thalamus/Connectors/
/// 5. Restart to unlock modules
/// </summary>
public partial class LicenseClaimWindow : Window
{
    // Cloudflare Worker URL (in production)
    private const string VENDING_MACHINE_URL = "https://dreamcraft-vending.rootedresilientshop.workers.dev/";

    // Local fallback for testing
    private const string LOCAL_VENDING_URL = "http://localhost:8787/";

    private HardwareInfo _hardwareInfo = new HardwareInfo();

    public LicenseClaimWindow()
    {
        InitializeComponent();
        InitializeHardwareDetection();
    }

    /// <summary>
    /// Detect and display the current machine's hardware
    /// </summary>
    private void InitializeHardwareDetection()
    {
        try
        {
            _hardwareInfo = GetCurrentHardwareInfo();
            HardwareInfoDisplay.Text = $"CPU: {_hardwareInfo.CpuId}\nDisk: {_hardwareInfo.DiskSerial}\nPlatform: {_hardwareInfo.Platform}\nHostname: {_hardwareInfo.Hostname}";
            StatusMessage.Text = "Ready to claim. Enter your purchase code and click 'Claim License'.";
        }
        catch (Exception ex)
        {
            HardwareInfoDisplay.Text = $"Error detecting hardware: {ex.Message}";
            StatusMessage.Foreground = System.Windows.Media.Brushes.Red;
            StatusMessage.Text = "Could not detect hardware info. Contact support if this persists.";
        }
    }

    /// <summary>
    /// Claim a license by contacting the vending machine
    /// </summary>
    private async void OnClaimClick(object sender, RoutedEventArgs e)
    {
        var purchaseCode = PurchaseCodeInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(purchaseCode))
        {
            ShowError("Please enter a purchase code.");
            return;
        }

        ClaimButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ProgressIndicator.Visibility = Visibility.Visible;
        ErrorMessage.Visibility = Visibility.Collapsed;
        StatusMessage.Text = "Claiming license...";

        try
        {
            // Prepare the request
            var requestBody = new
            {
                purchase_code = purchaseCode,
                hardware_id = JsonSerializer.Serialize(new
                {
                    cpu_id = _hardwareInfo.CpuId,
                    disk_serial = _hardwareInfo.DiskSerial,
                    platform = _hardwareInfo.Platform,
                    hostname = _hardwareInfo.Hostname,
                })
            };

            // Call the vending machine (try Cloudflare first, fallback to local)
            var certificate = await CallVendingMachine(requestBody);

            if (certificate == null)
            {
                ShowError("Failed to claim license. Please check your purchase code and try again.");
                return;
            }

            // Save the key to disk
            var module = ExtractModuleId(certificate);
            var keyPath = Path.Combine("d:/Projects/Thalamus/Connectors", $"{module}.key");
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            File.WriteAllText(keyPath, certificate);

            // Success!
            StatusMessage.Foreground = System.Windows.Media.Brushes.Lime;
            StatusMessage.Text = $"✅ License claimed! Key saved to {keyPath}.\n\nRestart Thalamus to unlock the module.";
            MessageBox.Show(
                $"License successfully claimed!\n\nThe key has been saved to:\n{keyPath}\n\nPlease restart Thalamus to activate the module.",
                "License Claimed",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );

            // Close after success
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError($"Error claiming license: {ex.Message}");
        }
        finally
        {
            ClaimButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            ProgressIndicator.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Call the Cloudflare vending machine
    /// </summary>
    private async System.Threading.Tasks.Task<string> CallVendingMachine(object requestBody)
    {
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
        {
            try
            {
                // Try Cloudflare first
                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync(VENDING_MACHINE_URL, content);

                if (!response.IsSuccessStatusCode)
                {
                    // Try local fallback
                    response = await client.PostAsync(LOCAL_VENDING_URL, content);
                }

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync();
                }

                var errorMsg = await response.Content.ReadAsStringAsync();
                throw new Exception($"Vending machine returned {response.StatusCode}: {errorMsg}");
            }
            catch (HttpRequestException ex)
            {
                // Network error - might be offline
                throw new Exception("Network error: Cannot reach license server. Check your internet connection.", ex);
            }
        }
    }

    /// <summary>
    /// Extract module_id from the certificate JSON
    /// </summary>
    private string ExtractModuleId(string certificateJson)
    {
        try
        {
            using (var doc = JsonDocument.Parse(certificateJson))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("marketplace_metadata", out var metadata) &&
                    metadata.TryGetProperty("module_id", out var moduleId))
                {
                    return moduleId.GetString() ?? "unknown";
                }
            }
        }
        catch { }
        return "unknown";
    }

    /// <summary>
    /// Get current system hardware information
    /// </summary>
    private HardwareInfo GetCurrentHardwareInfo()
    {
        var info = new HardwareInfo();

        // Platform
        info.Platform = Environment.OSVersion.Platform == PlatformID.Win32NT ? "Windows" : "Linux";

        // Hostname
        info.Hostname = Environment.MachineName;

        // CPU ID (simplified - in production would use WMI)
        try
        {
            info.CpuId = GetProcessorId() ?? "UNKNOWN_CPU";
        }
        catch
        {
            info.CpuId = "UNKNOWN_CPU";
        }

        // Disk Serial (simplified)
        try
        {
            info.DiskSerial = GetDiskSerial() ?? "UNKNOWN_DISK";
        }
        catch
        {
            info.DiskSerial = "UNKNOWN_DISK";
        }

        return info;
    }

    /// <summary>
    /// Get processor ID from WMI (Windows only)
    /// </summary>
    private string GetProcessorId()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            return "UNKNOWN_CPU_LINUX";

        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/C wmic cpu get ProcessorId /value",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            foreach (var line in output.Split('\n'))
            {
                if (line.StartsWith("ProcessorId="))
                {
                    return line.Replace("ProcessorId=", "").Trim();
                }
            }
        }
        catch { }

        return "UNKNOWN_CPU";
    }

    /// <summary>
    /// Get primary disk serial (Windows only)
    /// </summary>
    private string GetDiskSerial()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            return "UNKNOWN_DISK_LINUX";

        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/C wmic logicaldisk get SerialNumber",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            var lines = output.Split('\n');
            if (lines.Length > 1)
            {
                return lines[1].Trim();
            }
        }
        catch { }

        return "UNKNOWN_DISK";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ErrorMessage.Visibility = Visibility.Visible;
        StatusMessage.Foreground = System.Windows.Media.Brushes.Red;
        StatusMessage.Text = "An error occurred. See details below.";
    }
}

/// <summary>
/// Simple hardware info holder
/// </summary>
public class HardwareInfo
{
    public string CpuId { get; set; } = "UNKNOWN";
    public string DiskSerial { get; set; } = "UNKNOWN";
    public string Platform { get; set; } = "Unknown";
    public string Hostname { get; set; } = Environment.MachineName;
}

using System;
using Microsoft.Extensions.Logging;
using SnmpSharpNet;

namespace PrintPlusService.Services
{
    public enum PrinterIssue
    {
        None,
        OutOfPaper,
        Jammed
    }

    public class PrinterStatusChecker
    {
        private readonly ILogger<PrinterStatusChecker> _logger;

        public PrinterStatusChecker(ILogger<PrinterStatusChecker> logger)
        {
            _logger = logger;
        }

        public PrinterIssue CheckPrinterStatus(string printerIp)
        {
            try
            {
                UdpTarget target = new UdpTarget(System.Net.IPAddress.Parse(printerIp), 161, 2000, 1);
                OctetString community = new OctetString("public"); // Adjust if SNMP community is different

                Pdu pdu = new Pdu(PduType.Get);
                // hrPrinterDetectedErrorState OID
                pdu.VbList.Add("1.3.6.1.2.1.25.3.5.1.3.1");

                AgentParameters param = new AgentParameters(community)
                {
                    Version = SnmpVersion.Ver1
                };

                SnmpV1Packet result = (SnmpV1Packet)target.Request(pdu, param);

                if (result != null && result.Pdu.ErrorStatus == 0)
                {
                    var value = result.Pdu.VbList[0].Value as OctetString;
                    if (value != null && value.Length > 0)
                    {
                        int errorFlags = value[0];

                        _logger.LogInformation($"SNMP printer error flags byte: {Convert.ToString(errorFlags, 2).PadLeft(8, '0')}");

                        if ((errorFlags & (1 << 1)) != 0) // Bit 1 = noPaper
                        {
                            _logger.LogWarning("Printer is out of paper");
                            return PrinterIssue.OutOfPaper;
                        }

                        if ((errorFlags & (1 << 5)) != 0) // Bit 5 = jammed
                        {
                            _logger.LogWarning("Printer is jammed");
                            return PrinterIssue.Jammed;
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("SNMP query failed or printer did not respond properly.");
                }

                target.Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"SNMP error while checking printer status for {printerIp}");
            }

            return PrinterIssue.None;
        }
    }
}

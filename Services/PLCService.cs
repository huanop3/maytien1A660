using System;
using System.Threading;
using System.Threading.Tasks;
using S7.Net;

namespace Webapp_tien_server.Services
{
    public class PlcService
    {
        private Plc _plc;
        private readonly string _ipAddress = "192.168.0.1";
        private readonly short _rack = 0;
        private readonly short _slot = 1;
        private bool _isConnecting = false;
        private readonly int _dbNumber = 1; // DB1 as specified
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private byte[] _cachedDbData = new byte[4];
        private DateTime _lastReadTime = DateTime.MinValue;
        private readonly TimeSpan _cacheTimeout = TimeSpan.FromMilliseconds(100);

        public PlcService()
        {
            _plc = new Plc(CpuType.S71200, _ipAddress, _rack, _slot);
        }

        public async Task<(bool success, string message)> ConnectAsync()
        {
            if (_isConnecting)
                return (false, "Đang trong quá trình kết nối, vui lòng đợi...");
                
            await _semaphore.WaitAsync();
            _isConnecting = true;
            
            try
            {
                // Đóng kết nối hiện có trước
                if (_plc.IsConnected)
                {
                    await Task.Run(() => _plc.Close());
                }
                
                // Tạo mới đối tượng Plc để tránh vấn đề với NetworkStream đã bị dispose
                _plc = new Plc(CpuType.S71200, _ipAddress, _rack, _slot);
                
                // Thử mở kết nối với timeout
                var connectTask = Task.Run(() => _plc.Open());
                
                // Đặt timeout 5 giây cho kết nối
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask)
                {
                    // Kết nối quá thời gian
                    return (false, "Kết nối quá thời gian - kiểm tra IP và kết nối mạng");
                }
                
                // Đảm bảo task đã hoàn thành
                await connectTask;
                
                if (_plc.IsConnected)
                {
                    // Đọc dữ liệu ban đầu vào cache
                    await RefreshDbDataCache();
                    return (true, "Kết nối thành công");
                }
                else
                {
                    return (false, "Kết nối thất bại - PLC không báo cáo là đã kết nối");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi kết nối: {ex.Message}");
            }
            finally
            {
                _isConnecting = false;
                _semaphore.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                if (_plc.IsConnected)
                {
                    await Task.Run(() => _plc.Close());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi khi ngắt kết nối: {ex.Message}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task RefreshDbDataCache()
        {
            if (!_plc.IsConnected)
                return;

            try
            {
                _cachedDbData = await Task.Run(() => _plc.ReadBytes(DataType.DataBlock, _dbNumber, 0, 4));
                _lastReadTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi khi làm mới cache: {ex.Message}");
                throw;
            }
        }

        public async Task<bool> ReadDbWebserverBit(string bitName)
        {
            await _semaphore.WaitAsync();
            try
            {
                if (!_plc.IsConnected)
                    return false;

                // Kiểm tra xem cache có cần làm mới không
                if (DateTime.Now - _lastReadTime > _cacheTimeout)
                {
                    await RefreshDbDataCache();
                }
                
                // Get the bit position based on the variable name
                var bitPosition = GetBitPosition(bitName);
                if (!bitPosition.HasValue)
                    return false;
                
                // Extract the bit value from the byte array
                int byteIndex = bitPosition.Value / 8;
                int bitIndex = bitPosition.Value % 8;
                
                if (byteIndex >= _cachedDbData.Length)
                    return false;
                
                // Check if the specific bit is set
                return (_cachedDbData[byteIndex] & (1 << bitIndex)) != 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi đọc bit {bitName}: {ex.Message}");
                
                // Nếu lỗi liên quan đến NetworkStream, thử kết nối lại
                if (ex.Message.Contains("NetworkStream") || ex.Message.Contains("transport connection"))
                {
                    try
                    {
                        // Tạo mới đối tượng Plc
                        _plc = new Plc(CpuType.S71200, _ipAddress, _rack, _slot);
                        await Task.Run(() => _plc.Open());
                        
                        if (_plc.IsConnected)
                        {
                            await RefreshDbDataCache();
                        }
                    }
                    catch
                    {
                        // Bỏ qua lỗi khi thử kết nối lại
                    }
                }
                
                return false;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<bool> WriteDbWebserverBit(string bitName, bool value)
        {
            await _semaphore.WaitAsync();
            try
            {
                if (!_plc.IsConnected)
                    return false;

                // Get the bit position based on the variable name
                var bitPosition = GetBitPosition(bitName);
                if (!bitPosition.HasValue)
                    return false;
                
                // Calculate the DB address in the format DB{number}.DBX{byte}.{bit}
                int byteIndex = bitPosition.Value / 8;
                int bitIndex = bitPosition.Value % 8;
                string formattedAddress = $"DB{_dbNumber}.DBX{byteIndex}.{bitIndex}";
                
                // Write the value to the PLC
                await Task.Run(() => _plc.Write(formattedAddress, value));
                
                // Update the cache after writing
                await RefreshDbDataCache();
                
                // Verify the value was written using the cache
                int verifyByteIndex = bitPosition.Value / 8;
                int verifyBitIndex = bitPosition.Value % 8;
                bool verifyValue = (_cachedDbData[verifyByteIndex] & (1 << verifyBitIndex)) != 0;
                
                return verifyValue == value;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi ghi bit {bitName}: {ex.Message}");
                
                // Nếu lỗi liên quan đến NetworkStream, thử kết nối lại
                if (ex.Message.Contains("NetworkStream") || ex.Message.Contains("transport connection"))
                {
                    try
                    {
                        // Tạo mới đối tượng Plc
                        _plc = new Plc(CpuType.S71200, _ipAddress, _rack, _slot);
                        await Task.Run(() => _plc.Open());
                    }
                    catch
                    {
                        // Bỏ qua lỗi khi thử kết nối lại
                    }
                }
                
                return false;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        // Helper method to get the bit position (0-based) for each variable
        private int? GetBitPosition(string bitName)
        {
            // Map each bit name to its position in the DB
            switch (bitName)
            {
                case "DB_RH": return 0;  // Byte 0, Bit 0
                case "DB_RG": return 1;  // Byte 0, Bit 1
                case "DB_KBR": return 2; // Byte 0, Bit 2
                case "DB_DBT": return 3; // Byte 0, Bit 3
                case "DB_TD": return 4;  // Byte 0, Bit 4
                case "DB_CTC1": return 5; // Byte 0, Bit 5
                case "DB_CTC2": return 6; // Byte 0, Bit 6
                case "DB_CTT_DG": return 7; // Byte 0, Bit 7
                
                // ... rest of the mapping remains the same ...
                case "DB_K3": return 8;  // Byte 1, Bit 0
                case "DB_K2": return 9;  // Byte 1, Bit 1
                case "DB_RRH": return 10; // Byte 1, Bit 2
                case "DB_RDG": return 11; // Byte 1, Bit 3
                case "DB_N": return 12;  // Byte 1, Bit 4
                case "DB_T": return 13;  // Byte 1, Bit 5
                case "DB_KT": return 14; // Byte 1, Bit 6
                case "DB_KN": return 15; // Byte 1, Bit 7
                
                case "DB_K4": return 16; // Byte 2, Bit 0
                case "DB_DH1": return 17; // Byte 2, Bit 1
                case "DB_DH2": return 18; // Byte 2, Bit 2
                case "DB_C": return 19;  // Byte 2, Bit 3
                case "DB_chayThuan": return 20; // Byte 2, Bit 4
                case "DB_chayNghich": return 21; // Byte 2, Bit 5
                case "DB_m1": return 22; // Byte 2, Bit 6
                case "DB_D": return 23;  // Byte 2, Bit 7
                
                case "DB_m2": return 24; // Byte 3, Bit 0
                case "DB_M3": return 25; // Byte 3, Bit 1
                case "DB_TT": return 26; // Byte 3, Bit 2
                case "DB_TN": return 27; // Byte 3, Bit 3
                
                default:
                    Console.WriteLine($"Bit name '{bitName}' không được hỗ trợ");
                    return null;
            }
        }
    }
}
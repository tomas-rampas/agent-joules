namespace AgentJoules.Nvml.Interop;

/// <summary>Return codes of the NVML functions; the values come from nvml.h, see https://docs.nvidia.com/deploy/nvml-api/api/group__nvmlDeviceEnums.html#_CPPv412nvmlReturn_t.</summary>
public enum NvmlReturn
{
    Success = 0,
    Uninitialized = 1,
    InvalidArgument = 2,
    NotSupported = 3,
}
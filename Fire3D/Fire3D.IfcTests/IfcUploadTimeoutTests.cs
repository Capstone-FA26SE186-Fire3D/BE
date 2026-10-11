using Fire3D.Application.Ifc;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class IfcUploadTimeoutTests
{
    [Fact]
    public void Completion_deadline_scales_with_two_reads_and_is_capped()
    {
        var options=new IfcUploadOptions();
        Assert.Equal(30,options.CompletionTimeout(0).TotalSeconds,1);
        // 300 MiB at 5 MiB/s, read twice: 120 s plus the fixed 30 s.
        Assert.Equal(150,options.CompletionTimeout(300L*1024*1024).TotalSeconds,1);
        Assert.Equal(900,options.CompletionTimeout(50L*1024*1024*1024).TotalSeconds,1);
        Assert.Equal(60,new IfcUploadOptions{MinThroughputBytesPerSecond=10L*1024*1024}.CompletionTimeout(150L*1024*1024).TotalSeconds,1);
        Assert.Equal(30,new IfcUploadOptions{MaxCompletionSeconds=5}.CompletionTimeout(1L*1024*1024*1024).TotalSeconds,1);
    }
}

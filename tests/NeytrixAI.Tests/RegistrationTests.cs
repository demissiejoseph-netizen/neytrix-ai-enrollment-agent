using NeytrixAI.Domain.Entities;
using Xunit;

namespace NeytrixAI.Tests;

public class RegistrationTests
{
    private static Registration NewRegistration() =>
        Registration.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void StartsInInquiryState()
    {
        var reg = NewRegistration();
        Assert.Equal(RegistrationStatus.Inquiry, reg.Status);
        Assert.False(reg.IsEnrolled);
    }

    [Fact]
    public void CannotEnrollWithoutWaiverSigned()
    {
        var reg = NewRegistration();
        reg.MarkPaymentComplete(10000, "pi_123");

        var ex = Assert.Throws<InvalidOperationException>(() => reg.CompleteEnrollment());
        Assert.Contains("waiver", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CannotEnrollWithoutPaymentComplete()
    {
        var reg = NewRegistration();
        reg.MarkWaiverSent();
        reg.MarkWaiverSigned();

        var ex = Assert.Throws<InvalidOperationException>(() => reg.CompleteEnrollment());
        Assert.Contains("payment", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnrollsOnlyWhenWaiverSignedAndPaymentComplete()
    {
        var reg = NewRegistration();
        reg.MarkWaiverSent();
        reg.MarkWaiverSigned();
        reg.MarkPaymentComplete(10000, "pi_123");

        reg.CompleteEnrollment();

        Assert.True(reg.IsEnrolled);
        Assert.Equal(RegistrationStatus.Enrolled, reg.Status);
        Assert.NotNull(reg.EnrolledAt);
    }

    [Fact]
    public void WaitlistedRegistrationStartsWaitlisted()
    {
        var reg = Registration.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            isWaitlisted: true, waitlistPosition: 3);

        Assert.True(reg.IsWaitlisted);
        Assert.Equal(3, reg.WaitlistPosition);
    }

    [Fact]
    public void CancelledRegistrationCannotBeModified()
    {
        var reg = NewRegistration();
        reg.Cancel("guardian withdrew");

        Assert.Throws<InvalidOperationException>(() => reg.MarkWaiverSent());
    }
}

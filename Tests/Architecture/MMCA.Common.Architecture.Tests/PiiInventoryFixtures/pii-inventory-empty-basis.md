# Privacy Policy (fixture)

Fixture for PiiInventoryTestsBase. The inventory must also list the [Pii] members of the other
fixture in this test assembly (DataSubjectSample), because the gate scans the whole assembly.

## Personal-data inventory

| Member | Purpose | Lawful basis | Retention |
| --- | --- | --- | --- |
| `FixturePiiSubject.Email` | Sign-in and account email | Contract (GDPR Art. 6(1)(b)) | While the account exists |
| `FixturePiiSubject.Phone` | Delivery contact |  | 90 days after the last order |
| `DataSubjectSample.Email` | Representative data subject in PiiErasureContractFitnessTests | Contract | Until erasure |
| `DataSubjectSample.FullName` | Representative data subject in PiiErasureContractFitnessTests | Contract | Until erasure |

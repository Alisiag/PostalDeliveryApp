using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Npgsql;


public record ClientDto(int Id, string FullName, string Phone);
public record ParcelDetailDto(string TrackingNumber, string Sender, string Receiver, string Status);


public interface IClientRepository
{
    Task<IEnumerable<ClientDto>> GetActiveClientsAsync();
    Task<int> CreateClientAsync(string fullName, string phone, int userId);
    Task SoftDeleteClientAsync(int clientId, int userId);
}

public interface IParcelRepository
{
    Task<IEnumerable<ParcelDetailDto>> GetParcelDetailsAsync();
    Task CreateParcelAsync(string tracking, int senderId, int receiverId, int fromBranch, int toBranch, decimal weight, int userId);
    Task UpdateStatusAsync(int parcelId, int statusId, int branchId, int userId);
    Task SoftDeleteParcelAsync(int parcelId, int userId);
}

public interface IUnitOfWork : IDisposable
{
    IClientRepository Clients { get; }
    IParcelRepository Parcels { get; }
    Task CommitAsync();
    Task RollbackAsync();
}


public class ClientRepository : IClientRepository
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    public ClientRepository(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async Task<IEnumerable<ClientDto>> GetActiveClientsAsync()
    {
        var list = new List<ClientDto>();
        await using var cmd = new NpgsqlCommand("SELECT id, fullname, phone FROM vw_ActiveClients", _connection, _transaction);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new ClientDto(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }
        return list;
    }


    public async Task<int> CreateClientAsync(string fullName, string phone, int userId)
    {
        await using var cmd = new NpgsqlCommand("CALL sp_CreateClient(@name, @phone, @userId, @newId)", _connection, _transaction);
        cmd.Parameters.AddWithValue("name", fullName);
        cmd.Parameters.AddWithValue("phone", phone);
        cmd.Parameters.AddWithValue("userId", userId);

        var outParam = new NpgsqlParameter("newId", NpgsqlTypes.NpgsqlDbType.Integer)
        {
            Direction = ParameterDirection.InputOutput,
            Value = DBNull.Value
        };
        cmd.Parameters.Add(outParam);

        await cmd.ExecuteNonQueryAsync();
        return (int)outParam.Value!;
    }

    public async Task SoftDeleteClientAsync(int clientId, int userId)
    {
        await using var cmd = new NpgsqlCommand("CALL sp_SoftDeleteClient(@id, @userId)", _connection, _transaction);
        cmd.Parameters.AddWithValue("id", clientId);
        cmd.Parameters.AddWithValue("userId", userId);
        await cmd.ExecuteNonQueryAsync();
    }
}

public class ParcelRepository : IParcelRepository
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    public ParcelRepository(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async Task<IEnumerable<ParcelDetailDto>> GetParcelDetailsAsync()
    {
        var list = new List<ParcelDetailDto>();
        await using var cmd = new NpgsqlCommand("SELECT trackingnumber, sender, receiver, status FROM vw_ParcelDetails", _connection, _transaction);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new ParcelDetailDto(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        return list;
    }

    public async Task CreateParcelAsync(string tracking, int senderId, int receiverId, int fromBranch, int toBranch, decimal weight, int userId)
    {
        await using var cmd = new NpgsqlCommand(
            "CALL sp_CreateParcel(@tracking, @sender, @receiver, @from, @to, @weight, @userId)",
            _connection, _transaction);

        cmd.Parameters.AddWithValue("tracking", tracking);
        cmd.Parameters.AddWithValue("sender", senderId);
        cmd.Parameters.AddWithValue("receiver", receiverId);
        cmd.Parameters.AddWithValue("from", fromBranch);
        cmd.Parameters.AddWithValue("to", toBranch);
        cmd.Parameters.AddWithValue("weight", weight);
        cmd.Parameters.AddWithValue("userId", userId);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateStatusAsync(int parcelId, int statusId, int branchId, int userId)
    {
        await using var cmd = new NpgsqlCommand("CALL sp_UpdateParcelStatus(@id, @status, @branch, @userId)", _connection, _transaction);
        cmd.Parameters.AddWithValue("id", parcelId);
        cmd.Parameters.AddWithValue("status", statusId);
        cmd.Parameters.AddWithValue("branch", branchId);
        cmd.Parameters.AddWithValue("userId", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SoftDeleteParcelAsync(int parcelId, int userId)
    {
        await using var cmd = new NpgsqlCommand("CALL sp_SoftDeleteParcel(@id, @userId)", _connection, _transaction);
        cmd.Parameters.AddWithValue("id", parcelId);
        cmd.Parameters.AddWithValue("userId", userId);
        await cmd.ExecuteNonQueryAsync();
    }
}


public class UnitOfWork : IUnitOfWork
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    public IClientRepository Clients { get; }
    public IParcelRepository Parcels { get; }

    public UnitOfWork(string connectionString)
    {
        _connection = new NpgsqlConnection(connectionString);
        _connection.Open();
        _transaction = _connection.BeginTransaction();

        Clients = new ClientRepository(_connection, _transaction);
        Parcels = new ParcelRepository(_connection, _transaction);
    }

    public async Task CommitAsync() => await _transaction.CommitAsync();
    public async Task RollbackAsync() => await _transaction.RollbackAsync();

    public void Dispose()
    {
        _transaction.Dispose();
        _connection.Dispose();
    }
}


class Program
{
    static async Task Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string connString = "Host=localhost;Port=5432;Database=delivery_logistics_db;Username=postgres;Password=1234";

        using var uow = new UnitOfWork(connString);
        try
        {
 
            int senderId = await uow.Clients.CreateClientAsync("Іван Петренко", "+380501112233", userId: 1);
            int receiverId = await uow.Clients.CreateClientAsync("Олена Коваленко", "+380679998877", userId: 1);

            string trackNum = $"UA-{DateTime.Now.Ticks % 100000}";
            await uow.Parcels.CreateParcelAsync(trackNum, senderId, receiverId, fromBranch: 1, toBranch: 2, weight: 2.5m, userId: 1);

            await uow.CommitAsync();
            Console.WriteLine("Транзакція успішно завершена!\n");
        }
        catch (Exception ex)
        {
            await uow.RollbackAsync();
            Console.WriteLine($"Помилка, зміни скасовано: {ex.Message}");
            return;
        }

        using var readUow = new UnitOfWork(connString);
        var parcels = await readUow.Parcels.GetParcelDetailsAsync();

        Console.WriteLine("Активні посилки (з vw_ParcelDetails):");
        foreach (var p in parcels)
        {
            Console.WriteLine($"[{p.TrackingNumber}] {p.Sender} -> {p.Receiver} | Статус: {p.Status}");
        }
    }
}
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UncoveringGreatnessCRM.Domain;
using UncoveringGreatnessCRM.Models;
using UncoveringGreatnessCRM.Security;
using UncoveringGreatnessCRM.Services;

namespace UncoveringGreatnessCRM.Controllers;

[Authorize(Policy = "AdminOnly")]
public class UsersController : AppController
{
    private readonly UserService _users;
    private readonly AuthService _auth;
    private readonly ICurrentUser _me;
    private readonly IAuditService _audit;
    public UsersController(UserService users, AuthService auth, ICurrentUser me, IAuditService audit) { _users = users; _auth = auth; _me = me; _audit = audit; }

    public async Task<IActionResult> Index(string? q)
    {
        var r = await _users.ListAsync(q);
        if (!r.Succeeded) return Denied(r) ?? NotFound();
        ViewBag.Q = q;
        return View(r.Value);
    }

    [HttpGet]
    public IActionResult Create() => View(new UserCreateInput { TemporaryPassword = PasswordService.GenerateTemporary() });

    [HttpPost]
    public async Task<IActionResult> Create(UserCreateInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _users.CreateAsync(input);
            if (r.Succeeded) { Flash($"User created. Share the temporary password with {r.Value!.FullName} securely — they must change it at first sign-in."); return RedirectToAction(nameof(Index)); }
            AddErrors(r);
        }
        return View(input);
    }

    private async Task<bool> LoadEditLookupsAsync(int id)
    {
        var u = await _users.GetAsync(id);
        if (!u.Succeeded) return false;
        ViewBag.User = u.Value;
        ViewBag.Targets = (await _users.OptionsAsync()).Where(o => o.Id != id && o.Role != UserRole.Staff).ToList();
        ViewBag.IsSelf = id == _me.Id;
        return true;
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        if (!await LoadEditLookupsAsync(id)) return NotFound();
        var u = (UserSummary)ViewBag.User;
        return View(new UserUpdateInput { FullName = u.FullName, Email = u.Email, JobTitle = u.JobTitle, Phone = u.Phone, Role = u.Role, IsActive = u.IsActive });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, UserUpdateInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _users.UpdateAsync(id, input);
            if (r.Succeeded)
            {
                if (id == _me.Id)
                {
                    // Editing your own details: refresh this session so the new name/email show immediately (stamp is unchanged for self).
                    var self = await _auth.GetActiveUserAsync(id, null);
                    if (self.Succeeded)
                        await HttpContext.SignInAsync(AuthSchemes.Cookie, ClaimsFactory.Create(self.Value!, AuthSchemes.Cookie),
                            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
                }
                Flash("User updated."); return RedirectToAction(nameof(Index));
            }
            if (Denied(r) is { } d) return d;
            AddErrors(r);
        }
        if (!await LoadEditLookupsAsync(id)) return NotFound();
        return View(input);
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, int? transferToId)
    {
        var r = await _users.DeleteAsync(id, transferToId);
        if (!r.Succeeded)
        {
            if (r.Status == ResultStatus.NotFound) return NotFound();
            Flash(r.Error ?? "Could not delete user.", "error");
            return RedirectToAction(nameof(Edit), new { id });
        }
        Flash("User deleted. Their leads and tasks were transferred.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> ResetPassword(int id, AdminResetPasswordInput input)
    {
        var r = ModelState.IsValid ? await _users.ResetPasswordAsync(id, input.TemporaryPassword) : Result.Invalid("TemporaryPassword", "Enter a temporary password.");
        Flash(r.Succeeded ? "Password reset. The user must choose a new one at next sign-in and all their sessions were ended." : r.Error ?? "Could not reset password.", r.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Unlock(int id)
    {
        var r = await _users.UnlockAsync(id);
        Flash(r.Succeeded ? "Account unlocked." : r.Error ?? "Could not unlock account.", r.Succeeded ? "success" : "error");
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Audit(string? q, [FromQuery(Name = "action")] string? actionFilter, int page = 1)
    {
        // NOTE: a plain parameter called "action" would bind to the MVC route value ("Audit") instead of the
        // ?action= query string, so every filtered/unfiltered view would show nothing. Bind explicitly from the query.
        ViewBag.Q = q; ViewBag.Action = actionFilter; ViewBag.Actions = await _audit.ActionsAsync();
        return View(await _audit.SearchAsync(q, actionFilter, page, 50));
    }
}
